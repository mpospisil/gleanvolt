using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.HomeAssistant;

/// <summary>
/// Publishes the controller to Home Assistant over MQTT: on connect it sends retained discovery
/// configs (so HA auto-creates the device + entities), marks itself available, and subscribes to the
/// charge-control switch command; it then republishes status periodically. An incoming switch command
/// toggles charge control at runtime. Disabled by default.
/// </summary>
public sealed class HomeAssistantMqttWorker : BackgroundService
{
    private readonly HomeAssistantOptions _options;
    private readonly HaDiscovery _discovery;
    private readonly IChargeControlModeSelector _mode;
    private readonly IBatteryHoldSelector _batteryHold;
    private readonly IForecastRuntimeSettings _forecastSettings;
    private readonly ITargetedChargeSelector _target;
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

    public HomeAssistantMqttWorker(
        IOptions<HomeAssistantOptions> options,
        IOptions<BatteryHoldOptions> batteryHoldOptions,
        IChargeControlModeSelector mode,
        IBatteryHoldSelector batteryHold,
        IForecastRuntimeSettings forecastSettings,
        ITargetedChargeSelector target,
        IServiceShutdown shutdown,
        ChargeControlStatusHolder statusHolder,
        ILogger<HomeAssistantMqttWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _batteryHoldEnabled = batteryHoldOptions.Value.Enabled;
        _discovery = new HaDiscovery(_options, _batteryHoldEnabled);
        _mode = mode;
        _batteryHold = batteryHold;
        _forecastSettings = forecastSettings;
        _target = target;
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
            .WithClientId($"gleanvolt-controller-{_options.DeviceId}")
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

        foreach (var (topic, payload) in _discovery.DiscoveryMessages())
        {
            await PublishAsync(topic, payload, retain: true, cancellationToken).ConfigureAwait(false);
        }

        await PublishAsync(_discovery.AvailabilityTopic, HaDiscovery.PayloadOnline, retain: true, cancellationToken).ConfigureAwait(false);
        await _client!.SubscribeAsync(_discovery.ModeCommandTopic, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_batteryHoldEnabled)
        {
            await _client.SubscribeAsync(_discovery.BatteryHoldCommandTopic, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var objectId in HaDiscovery.NumberObjectIds)
        {
            await _client.SubscribeAsync(_discovery.NumberCommandTopic(objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
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

        await PublishAsync(_discovery.ModeStateTopic, _discovery.ModeState(_mode.Mode), retain: true, cancellationToken).ConfigureAwait(false);

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

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        if (e.ApplicationMessage.Topic == _discovery.StopServiceCommandTopic)
        {
            return OnStopServiceCommandAsync(e.ApplicationMessage.ConvertPayloadToString());
        }

        if (e.ApplicationMessage.Topic == _discovery.ActivateTargetCommandTopic)
        {
            return OnActivateTargetAsync(e.ApplicationMessage.ConvertPayloadToString());
        }

        foreach (var objectId in HaDiscovery.TextObjectIds)
        {
            if (e.ApplicationMessage.Topic == _discovery.TextCommandTopic(objectId))
            {
                return OnTextCommandAsync(objectId, e.ApplicationMessage.ConvertPayloadToString());
            }
        }

        if (_batteryHoldEnabled && e.ApplicationMessage.Topic == _discovery.BatteryHoldCommandTopic)
        {
            return OnBatteryHoldCommandAsync(e.ApplicationMessage.ConvertPayloadToString());
        }

        foreach (var objectId in HaDiscovery.NumberObjectIds)
        {
            if (e.ApplicationMessage.Topic == _discovery.NumberCommandTopic(objectId))
            {
                return OnNumberCommandAsync(objectId, e.ApplicationMessage.ConvertPayloadToString());
            }
        }

        if (e.ApplicationMessage.Topic != _discovery.ModeCommandTopic)
        {
            return Task.CompletedTask;
        }

        var payload = e.ApplicationMessage.ConvertPayloadToString();
        if (HaDiscovery.TryParseMode(payload, out var mode))
        {
            _mode.Set(mode, "Home Assistant");
        }
        else
        {
            _logger.LogWarning("Ignoring unknown charge mode command '{Payload}'.", payload);
        }

        // Reflect the current mode back immediately so the HA select settles.
        _ = PublishAsync(_discovery.ModeStateTopic, _discovery.ModeState(_mode.Mode), retain: true, CancellationToken.None);
        return Task.CompletedTask;
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
            default:
                return Task.CompletedTask;
        }

        // Echo back what was actually stored (the setters clamp), so a rejected value doesn't leave the
        // HA box showing something the controller isn't using.
        return PublishNumberStatesAsync(CancellationToken.None);
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
    private Task OnActivateTargetAsync(string? payload)
    {
        if (!HaDiscovery.IsPress(payload))
        {
            _logger.LogWarning("Ignoring '{Payload}' on the activate-target topic.", payload);
            return Task.CompletedTask;
        }

        if (_pendingEnergyKWh <= 0 || _pendingDeparture is not { } departure)
        {
            _logger.LogWarning(
                "Activate target pressed with nothing to activate: set both Target energy and Departure time first.");
            return Task.CompletedTask;
        }

        var now = _timeProvider.GetUtcNow();
        if (departure <= now)
        {
            _logger.LogWarning(
                "Activate target pressed with a departure already past ({Departure}); set it again.",
                departure.LocalDateTime);
            return Task.CompletedTask;
        }

        _target.Set(new TargetedChargeRequest(_pendingEnergyKWh * 1000, departure, now), "Home Assistant");
        _mode.Set(Core.Enums.ChargeControlMode.Targeted, "Home Assistant");

        return PublishAsync(_discovery.ModeStateTopic, _discovery.ModeState(_mode.Mode), retain: true, CancellationToken.None);
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
