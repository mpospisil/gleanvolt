using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using Solax.Core.Interfaces;
using Solax.Worker.Configuration;

namespace Solax.Worker.HomeAssistant;

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
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ILogger<HomeAssistantMqttWorker> _logger;
    private IMqttClient? _client;

    public HomeAssistantMqttWorker(
        IOptions<HomeAssistantOptions> options,
        IChargeControlModeSelector mode,
        ChargeControlStatusHolder statusHolder,
        ILogger<HomeAssistantMqttWorker> logger)
    {
        _options = options.Value;
        _discovery = new HaDiscovery(_options);
        _mode = mode;
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
            .WithClientId($"solax-controller-{_options.DeviceId}")
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
        if (status is not null)
        {
            await PublishAsync(_discovery.StateTopic, _discovery.StateJson(status), retain: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
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
