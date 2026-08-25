using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.HomeAssistant;

/// <summary>
/// Publishes the controller to Home Assistant over MQTT: on connect it sends retained discovery
/// configs (so HA auto-creates the device + entities), marks itself available, and subscribes to every
/// command topic it published; it then republishes status periodically. An incoming press starts or
/// stops charging through <see cref="IChargeActions"/> — the same seam the web UI drives, so the two
/// surfaces cannot disagree. Disabled by default.
/// </summary>
public sealed class HomeAssistantMqttWorker : BackgroundService
{
    private readonly HomeAssistantOptions _options;
    private readonly HaDiscovery _discovery;
    private readonly IChargeActions _actions;
    private readonly IBatteryHoldSelector _batteryHold;
    private readonly IForecastRuntimeSettings _forecastSettings;
    private readonly ITargetedChargeSelector _target;
    private readonly IVehicleTelemetry? _vehicle;
    private readonly VehicleOptions _vehicleOptions;
    private readonly IServiceShutdown _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly bool _batteryHoldEnabled;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ILogger<HomeAssistantMqttWorker> _logger;
    private IMqttClient? _client;

    // The half-made request: what the number and the text entities hold until the button applies them.
    // It lives here rather than in the selector because an amount typed and not yet activated is not a
    // request -- the selector only ever holds something the controller is actually working to.
    private double _pendingEnergyKWh;
    private DateTimeOffset? _pendingDeparture;
    private string _pendingDepartureText = string.Empty;
    private TargetedChargePriority _pendingPriority = TargetedChargePriority.Cheapest;
    private double _pendingRestSocPercent;

    public HomeAssistantMqttWorker(
        IOptions<HomeAssistantOptions> options,
        IOptions<BatteryHoldOptions> batteryHoldOptions,
        IChargeActions actions,
        IBatteryHoldSelector batteryHold,
        IForecastRuntimeSettings forecastSettings,
        ITargetedChargeSelector target,
        IServiceShutdown shutdown,
        ChargeControlStatusHolder statusHolder,
        ILogger<HomeAssistantMqttWorker> logger,
        IOptions<TargetedChargeOptions> targetedOptions,
        IOptions<VehicleOptions> vehicleOptions,
        PvSystemInfo site,
        IVehicleTelemetry? vehicle = null,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _batteryHoldEnabled = batteryHoldOptions.Value.Enabled;
        _discovery = new HaDiscovery(_options, site, _batteryHoldEnabled);
        _actions = actions;
        _batteryHold = batteryHold;
        _forecastSettings = forecastSettings;
        _target = target;
        _vehicle = vehicle;
        _vehicleOptions = vehicleOptions.Value;
        _pendingRestSocPercent = targetedOptions.Value.JustInTime.RestSocPercent;
        _shutdown = shutdown;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _statusHolder = statusHolder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Home Assistant MQTT integration is disabled.");
            return;
        }

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            // One client id per system, not per unique-id root: two installations on one broker must not
            // fight over a session, and the topic prefix is what already tells them apart.
            .WithClientId($"gleanvolt-controller-{_discovery.SystemId}")
            .WithWillTopic(_discovery.AvailabilityTopic)
            .WithWillPayload(HaDiscovery.PayloadOffline)
            .WithWillRetain();

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            clientOptionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        var clientOptions = clientOptionsBuilder.Build();

        _logger.LogInformation(
            "Home Assistant MQTT integration enabled; broker {Host}:{Port}.", _options.BrokerHost, _options.BrokerPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(clientOptions, stoppingToken).ConfigureAwait(false);
                    await OnConnectedAsync(stoppingToken).ConfigureAwait(false);
                }

                await PublishStatusAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Home Assistant MQTT cycle failed; will retry.");
            }

            try
            {
                await Task.Delay(_options.StatusInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await MarkOfflineAndDisconnectAsync().ConfigureAwait(false);
    }

    private async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connected to MQTT broker; publishing Home Assistant discovery.");

        // Remove any entities from older versions (e.g. the previous on/off switch).
        foreach (var topic in _discovery.RetiredDiscoveryTopics())
        {
            await PublishAsync(topic, string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
        }

        // Retained state left under a prefix this installation no longer publishes on. Nothing reads it
        // once the configs above point elsewhere; clearing it is what keeps the broker readable.
        foreach (var topic in _discovery.RetiredStateTopics())
        {
            await PublishAsync(topic, string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (topic, payload) in _discovery.DiscoveryMessages())
        {
            await PublishAsync(topic, payload, retain: true, cancellationToken).ConfigureAwait(false);
        }

        await PublishAsync(_discovery.AvailabilityTopic, HaDiscovery.PayloadOnline, retain: true, cancellationToken).ConfigureAwait(false);

        foreach (var (objectId, _, _, _) in HaDiscovery.StartButtons)
        {
            await _client!.SubscribeAsync(_discovery.ButtonCommandTopic(objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _client!.SubscribeAsync(_discovery.ChargeOffCommandTopic, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_batteryHoldEnabled)
        {
            await _client.SubscribeAsync(_discovery.BatteryHoldCommandTopic, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var objectId in HaDiscovery.NumberObjectIds)
        {
            await _client.SubscribeAsync(_discovery.NumberCommandTopic(objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var objectId in HaDiscovery.SelectObjectIds)
        {
            await _client.SubscribeAsync(_discovery.SelectCommandTopic(objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var objectId in HaDiscovery.TextObjectIds)
        {
            await _client.SubscribeAsync(_discovery.TextCommandTopic(objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _client.SubscribeAsync(_discovery.ActivateTargetCommandTopic, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _client.SubscribeAsync(_discovery.StopServiceCommandTopic, cancellationToken: cancellationToken).ConfigureAwait(false);

        await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        if (_client is null || !_client.IsConnected)
        {
            return;
        }

        var status = _statusHolder.Current;

        if (_batteryHoldEnabled)
        {
            // Report what is actually armed on the inverter (status.BatteryHoldActive), not what was
            // asked for — a write that failed then shows in HA as the switch springing back to OFF.
            // Before the first poll there is no status yet, so fall back to the requested state.
            var held = status?.BatteryHoldActive ?? _batteryHold.Hold;
            await PublishAsync(_discovery.BatteryHoldStateTopic, HaDiscovery.SwitchState(held), retain: true, cancellationToken).ConfigureAwait(false);
        }

        await PublishNumberStatesAsync(cancellationToken).ConfigureAwait(false);
        await PublishTextStatesAsync(cancellationToken).ConfigureAwait(false);
        await PublishSelectStatesAsync(cancellationToken).ConfigureAwait(false);

        if (status is not null)
        {
            await PublishAsync(_discovery.StateTopic, _discovery.StateJson(status), retain: true, cancellationToken).ConfigureAwait(false);
        }
    }

    // The numbers carry the values the controller is actually using, so an HA restart (or a second
    // dashboard) sees the truth rather than whatever was last typed into a box.
    private async Task PublishNumberStatesAsync(CancellationToken cancellationToken)
    {
        foreach (var (objectId, value) in CurrentNumberValues())
        {
            await PublishAsync(
                _discovery.NumberStateTopic(objectId),
                value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                retain: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private IEnumerable<(string ObjectId, double Value)> CurrentNumberValues()
    {
        yield return (HaDiscovery.DailyEvTargetNumber, _forecastSettings.DailyEvTargetWh / 1000);
        yield return (HaDiscovery.SessionEnergyTargetNumber, _forecastSettings.SessionEnergyTargetWh / 1000);
        yield return (HaDiscovery.MinBatterySocNumber, _forecastSettings.MinBatterySocFloorPercent);
        yield return (HaDiscovery.ResumeMarginNumber, _forecastSettings.FloorResumeMarginPercent);

        // The active request outranks whatever was last typed: a target set from the web UI has to
        // show up here too, or the two surfaces would disagree about what the car is being charged to.
        yield return (HaDiscovery.TargetEnergyNumber, (_target.Request?.RequiredEnergyWh / 1000) ?? _pendingEnergyKWh);
        yield return (HaDiscovery.TargetRestSocNumber, _target.Request?.RestSocPercent ?? _pendingRestSocPercent);
    }

    // The departure as text, echoed back so a Home Assistant restart (or a second dashboard) sees what
    // is actually being worked to rather than an empty box.
    private Task PublishTextStatesAsync(CancellationToken cancellationToken)
    {
        var text = _target.Request is { } request
            ? TimeZoneInfo.ConvertTime(request.DepartBy, _timeProvider.LocalTimeZone).ToString("yyyy-MM-dd HH:mm")
            : _pendingDepartureText;

        return PublishAsync(_discovery.TextStateTopic(HaDiscovery.TargetDepartureText), text, retain: true, cancellationToken);
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e) =>
        HandleCommandAsync(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString());

    /// <summary>
    /// Routes one incoming command. Split out from the MQTTnet event so the command surface — which
    /// button does what, and what a payload that isn't a press does — can be tested without a broker.
    /// </summary>
    internal Task HandleCommandAsync(string topic, string? payload)
    {
        if (topic == _discovery.StopServiceCommandTopic)
        {
            return OnStopServiceCommandAsync(payload);
        }

        if (topic == _discovery.ActivateTargetCommandTopic)
        {
            return OnActivateTargetAsync(payload);
        }

        foreach (var (objectId, mode, _, _) in HaDiscovery.StartButtons)
        {
            if (topic == _discovery.ButtonCommandTopic(objectId))
            {
                return OnStartAsync(mode, payload);
            }
        }

        if (topic == _discovery.ChargeOffCommandTopic)
        {
            return OnChargeOffAsync(payload);
        }

        foreach (var objectId in HaDiscovery.SelectObjectIds)
        {
            if (topic == _discovery.SelectCommandTopic(objectId))
            {
                return OnSelectCommandAsync(objectId, payload);
            }
        }

        foreach (var objectId in HaDiscovery.SelectObjectIds)
        {
            if (topic == _discovery.SelectCommandTopic(objectId))
            {
                return OnSelectCommandAsync(objectId, payload);
            }
        }

        foreach (var objectId in HaDiscovery.TextObjectIds)
        {
            if (topic == _discovery.TextCommandTopic(objectId))
            {
                return OnTextCommandAsync(objectId, payload);
            }
        }

        if (_batteryHoldEnabled && topic == _discovery.BatteryHoldCommandTopic)
        {
            return OnBatteryHoldCommandAsync(payload);
        }

        foreach (var objectId in HaDiscovery.NumberObjectIds)
        {
            if (topic == _discovery.NumberCommandTopic(objectId))
            {
                return OnNumberCommandAsync(objectId, payload);
            }
        }

        return Task.CompletedTask;
    }

    // A press writes the charger's use-mode and only then selects the strategy, so a mode that could
    // not be started is not reported as running. There is no state topic to echo to: a button has none,
    // and what the dashboard shows next is the Charge mode sensor on the next status publish.
    private async Task OnStartAsync(ChargeControlMode mode, string? payload)
    {
        if (!HaDiscovery.IsPress(payload))
        {
            _logger.LogWarning("Ignoring '{Payload}' on the {Mode} start topic.", payload, mode);
            return;
        }

        var result = await _actions.StartAsync(mode, "Home Assistant").ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Starting {Mode} from Home Assistant failed: {Message}", mode, result.Message);
            return;
        }

        // Straight away rather than at the next StatusInterval: the sensor is the only feedback a
        // press gets, and a dashboard that shows nothing for 30 seconds reads as a button that did
        // nothing.
        await PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task OnChargeOffAsync(string? payload)
    {
        if (!HaDiscovery.IsPress(payload))
        {
            _logger.LogWarning("Ignoring '{Payload}' on the charge-off topic.", payload);
            return;
        }

        var result = await _actions.StopAsync("Home Assistant").ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Stopping charging from Home Assistant: {Message}", result.Message);
        }

        await PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // The one command with no echo and no state topic: a button has no state, and by the time Home
    // Assistant could read one this worker is on its way down. The acknowledgement the dashboard
    // actually sees is the availability topic going "offline" as the loop below exits, which
    // MarkOfflineAndDisconnectAsync publishes on the way out.
    //
    // Nothing is published from here on purpose -- the shutdown this starts disposes the client, and
    // racing a publish against that would only produce a logged failure in the last second of the run.
    private Task OnStopServiceCommandAsync(string? payload)
    {
        if (!HaDiscovery.IsPress(payload))
        {
            _logger.LogWarning(
                "Ignoring '{Payload}' on the stop-service topic; only '{Press}' stops the service.",
                payload,
                HaDiscovery.PayloadPress);
            return Task.CompletedTask;
        }

        _shutdown.RequestStop("Home Assistant");
        return Task.CompletedTask;
    }

    private Task OnBatteryHoldCommandAsync(string? payload)
    {
        if (HaDiscovery.TryParseSwitch(payload, out var hold))
        {
            _batteryHold.Set(hold, "Home Assistant");
        }
        else
        {
            _logger.LogWarning("Ignoring unknown battery hold command '{Payload}'.", payload);
        }

        // Echo the requested state so the HA switch settles immediately; the next status publish
        // replaces it with what is actually armed on the inverter.
        return PublishAsync(
            _discovery.BatteryHoldStateTopic, HaDiscovery.SwitchState(_batteryHold.Hold), retain: true, CancellationToken.None);
    }

    private Task OnNumberCommandAsync(string objectId, string? payload)
    {
        if (!double.TryParse(payload, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            _logger.LogWarning("Ignoring unparseable value '{Payload}' for {ObjectId}.", payload, objectId);
            return Task.CompletedTask;
        }

        switch (objectId)
        {
            case HaDiscovery.DailyEvTargetNumber:
                _forecastSettings.SetDailyEvTargetWh(value * 1000, "Home Assistant");
                break;
            case HaDiscovery.SessionEnergyTargetNumber:
                _forecastSettings.SetSessionEnergyTargetWh(value * 1000, "Home Assistant");
                break;
            case HaDiscovery.MinBatterySocNumber:
                _forecastSettings.SetMinBatterySocFloorPercent(value, "Home Assistant");
                break;
            case HaDiscovery.ResumeMarginNumber:
                _forecastSettings.SetFloorResumeMarginPercent(value, "Home Assistant");
                break;
            case HaDiscovery.TargetEnergyNumber:
                // Held, not applied: the button is what turns an amount into a promise.
                _pendingEnergyKWh = Math.Max(0, value);
                break;
            case HaDiscovery.TargetRestSocNumber:
                _pendingRestSocPercent = Math.Clamp(value, 0, 100);
                break;
            default:
                return Task.CompletedTask;
        }

        // Echo back what was actually stored (the setters clamp), so a rejected value doesn't leave the
        // HA box showing something the controller isn't using.
        return PublishNumberStatesAsync(CancellationToken.None);
    }

    // The priority as text, echoed back so a Home Assistant restart sees what is actually being worked
    // to. The running request outranks whatever was last selected, exactly as the energy number does.
    private Task PublishSelectStatesAsync(CancellationToken cancellationToken)
    {
        var priority = _target.Request?.Priority ?? _pendingPriority;

        return PublishAsync(
            _discovery.SelectStateTopic(HaDiscovery.TargetPrioritySelect),
            priority.ToString(),
            retain: true,
            cancellationToken);
    }

    private Task OnSelectCommandAsync(string objectId, string? payload)
    {
        if (objectId != HaDiscovery.TargetPrioritySelect)
        {
            return Task.CompletedTask;
        }

        if (!Enum.TryParse<TargetedChargePriority>(payload, ignoreCase: true, out var priority))
        {
            _logger.LogWarning(
                "Ignoring unrecognised charge priority '{Payload}'; expected Cheapest or JustInTime.", payload);
            return Task.CompletedTask;
        }

        // Held like the energy and the departure: a priority chosen and not yet activated is not a
        // request, and the button is what makes all four of them one.
        _pendingPriority = priority;
        return PublishSelectStatesAsync(CancellationToken.None);
    }

    private Task OnTextCommandAsync(string objectId, string? payload)
    {
        if (objectId != HaDiscovery.TargetDepartureText)
        {
            return Task.CompletedTask;
        }

        if (!HaDiscovery.TryParseDeparture(payload, _timeProvider.GetUtcNow(), _timeProvider.LocalTimeZone, out var departure))
        {
            _logger.LogWarning(
                "Ignoring unparseable departure time '{Payload}'; expected HH:mm or yyyy-MM-dd HH:mm.", payload);
            return Task.CompletedTask;
        }

        _pendingDeparture = departure;
        _pendingDepartureText = TimeZoneInfo.ConvertTime(departure, _timeProvider.LocalTimeZone).ToString("yyyy-MM-dd HH:mm");

        // Echoed as the resolved timestamp rather than what was typed: "07:00" and "tomorrow 07:00" are
        // the same request, and only one of them can be read back a day later without ambiguity.
        return PublishTextStatesAsync(CancellationToken.None);
    }

    // The two halves become a request here, in the same order the web page applies them: the request
    // first, then the mode, so the controller never sees a cycle of Targeted with nothing to aim at.
    private async Task OnActivateTargetAsync(string? payload)
    {
        if (!HaDiscovery.IsPress(payload))
        {
            _logger.LogWarning("Ignoring '{Payload}' on the activate-target topic.", payload);
            return;
        }

        if (_pendingEnergyKWh <= 0 || _pendingDeparture is not { } departure)
        {
            _logger.LogWarning(
                "Activate target pressed with nothing to activate: set both Target energy and Departure time first.");
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (departure <= now)
        {
            _logger.LogWarning(
                "Activate target pressed with a departure already past ({Departure}); set it again.",
                departure.LocalDateTime);
            return;
        }

        var energyWh = _pendingEnergyKWh * 1000;
        var request = new TargetedChargeRequest(energyWh, departure, now) with { Priority = _pendingPriority };

        // The same split the web tab makes, made in the same place in the sequence and by the same
        // arithmetic. It needs the car's SOC and a configured capacity: without both there is no honest
        // rest point, so the priority is honoured as far as it can be -- the request is still marked
        // JustInTime -- and the tail is zero, which means nothing is held. Warned about rather than
        // silently ignored, because a hold that quietly did not happen is the worst of the three.
        if (_pendingPriority == TargetedChargePriority.JustInTime)
        {
            var socNow = _vehicle?.GetCurrentState()?.SocPercent;
            var capacityWh = _vehicleOptions.BatteryCapacityKWh * 1000;
            var endSoc = VehicleTargetEnergy.ResultingSocPercent(
                socNow, energyWh, capacityWh, _vehicleOptions.ChargeEfficiency);

            var tailWh = endSoc is { } end
                ? VehicleTargetEnergy.TailAboveRestWh(
                    socNow, end, _pendingRestSocPercent, capacityWh, _vehicleOptions.ChargeEfficiency)
                : null;

            if (tailWh is null)
            {
                _logger.LogWarning(
                    "Just-in-time asked for, but there is no car SOC and/or no Vehicle:BatteryCapacityKWh to "
                    + "measure a {Rest:F0}% rest point from; charging without a hold.",
                    _pendingRestSocPercent);
            }

            request = request with
            {
                TailEnergyWh = Math.Clamp(tailWh ?? 0, 0, energyWh),
                RestSocPercent = _pendingRestSocPercent,
            };
        }

        _target.Set(request, "Home Assistant");
        await OnStartAsync(ChargeControlMode.Targeted, payload).ConfigureAwait(false);
    }

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkOfflineAndDisconnectAsync()
    {
        if (_client is null || !_client.IsConnected)
        {
            return;
        }

        try
        {
            await PublishAsync(_discovery.AvailabilityTopic, HaDiscovery.PayloadOffline, retain: true, CancellationToken.None).ConfigureAwait(false);
            await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanly disconnect from the MQTT broker.");
        }
    }

    public override void Dispose()
    {
        _client?.Dispose();
        base.Dispose();
    }
}
