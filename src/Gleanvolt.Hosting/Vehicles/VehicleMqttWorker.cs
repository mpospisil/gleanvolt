using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure.Vehicles;

namespace Gleanvolt.Hosting.Vehicles;

/// <summary>
/// Subscribes to the EV telemetry topic and pushes each usable message into
/// <see cref="VehicleStateHolder"/>. Disabled by default.
///
/// <para>Subscription-driven, not polled: the loop below exists only to establish and re-establish the
/// connection, and the data arrives whenever the publisher sends it. That is why this is a separate
/// worker from <c>HomeAssistantMqttWorker</c>, which is a periodic publisher on a
/// <c>StatusInterval</c> delay — the two lifecycles have nothing useful to share.</para>
///
/// <para>The publisher is expected to send <b>retained</b> messages, so a controller restart is handed
/// the last known reading on subscribe instead of waiting for the source's next update — which, for a
/// cloud-fed car, can be a quarter of an hour away.</para>
/// </summary>
public sealed class VehicleMqttWorker : BackgroundService
{
    private readonly VehicleOptions _options;
    private readonly VehicleStateHolder _holder;
    private readonly ILogger<VehicleMqttWorker> _logger;
    private IMqttClient? _client;

    // Rejections are logged on change rather than per message: a Home Assistant template publishing
    // junk would otherwise fill the log at the publisher's rate, which is exactly the noise this
    // project just finished removing from the Home Assistant side.
    private string? _lastError;
    private bool _receivedAnything;

    private readonly string _topic;

    /// <summary>
    /// What a reading off this topic is called when the publisher did not name it (issue #141).
    ///
    /// <para>The payload's <c>source</c> field is optional and the reference install fills it in, so
    /// this is the fallback rather than the usual answer. It exists because the week of running both
    /// feeds is counted <b>per source id</b>: an unnamed reading would be filed under "unnamed", which
    /// says nothing about which of the two feeds it came from — and the dashboard's "via ..." would
    /// stay blank for the one feed that has been working since #73.</para>
    ///
    /// <para>A label, and nothing more. Nothing dispatches on it, the parser is untouched, and the
    /// MQTT path keeps working exactly as it did.</para>
    /// </summary>
    public const string DefaultSourceId = "mqtt";

    /// <summary>
    /// Gives a reading this feed's name when the publisher did not give it one. Nothing else about the
    /// reading is touched; a payload that named itself keeps the name it chose.
    /// </summary>
    internal static VehicleState Label(VehicleState state) =>
        string.IsNullOrWhiteSpace(state.SourceId) ? state with { SourceId = DefaultSourceId } : state;

    /// <summary>
    /// The client id this worker connects with. Not namespaced by <c>Pv:Id</c> the way the Home
    /// Assistant worker's is — the machine is what distinguishes one subscriber here — so two
    /// controllers on one machine would collide. Shown on <c>/pv-system</c> (issue #143), which is how
    /// anyone ever finds that out.
    /// </summary>
    public static string ClientId => $"gleanvolt-vehicle-{Environment.MachineName}";

    /// <param name="ev">
    /// The car this feed reports on (#124). Only the topic comes from here: two cars on one broker are
    /// two topics, so the topic is genuinely per-vehicle, while the broker, the credentials and the
    /// staleness guard stay in the Vehicle section because they describe the feed rather than the car.
    /// </param>
    public VehicleMqttWorker(
        IOptions<VehicleOptions> options,
        EvInfo ev,
        VehicleStateHolder holder,
        ILogger<VehicleMqttWorker> logger)
    {
        _topic = ev.TelemetryTopic;
        _options = options.Value;
        _holder = holder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Vehicle telemetry is disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_topic))
        {
            _logger.LogWarning(
                "Vehicle telemetry is enabled but Ev:Vehicles:0:Telemetry:Topic is empty; nothing to subscribe to.");
            return;
        }

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .WithClientId(ClientId);

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            clientOptionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        var clientOptions = clientOptionsBuilder.Build();

        _logger.LogInformation(
            "Vehicle telemetry enabled; broker {Host}:{Port}, topic {Topic}, max age {MaxAge}.",
            _options.BrokerHost, _options.BrokerPort, _topic, _options.MaxAge);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(clientOptions, stoppingToken).ConfigureAwait(false);
                    await _client.SubscribeAsync(_topic, cancellationToken: stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Subscribed to vehicle telemetry on {Topic}.", _topic);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never fatal: the car is advisory data, and the controller must keep polling and
                // charging whether or not this feed is reachable.
                _logger.LogWarning(ex, "Vehicle telemetry connection failed; will retry.");
            }

            try
            {
                await Task.Delay(_options.ReconnectInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await DisconnectAsync().ConfigureAwait(false);
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var payload = e.ApplicationMessage.ConvertPayloadToString();

        if (!VehicleTelemetryPayload.TryParse(payload, out var state, out var error))
        {
            // The previous reading is deliberately left in place: "the reading is getting older" is a
            // diagnosable state, whereas blanking it would look identical to having no car at all.
            if (error != _lastError)
            {
                _logger.LogWarning(
                    "Ignoring vehicle telemetry on {Topic}: {Error}.", e.ApplicationMessage.Topic, error);
                _lastError = error;
            }

            return Task.CompletedTask;
        }

        _lastError = null;

        // Named if the publisher did not name it, so that the reading can be told apart from the
        // manufacturer feed's on the dashboard and in the week's tally (#141).
        state = Label(state!);

        _holder.Set(state);

        if (!_receivedAnything)
        {
            _receivedAnything = true;
            _logger.LogInformation(
                "First vehicle reading from {Topic}: SOC={Soc}% charge={ChargeState} plug={PlugState} captured {CapturedAt:O}.",
                e.ApplicationMessage.Topic, state.SocPercent, state.ChargeState, state.PlugState, state.CapturedAt);
        }
        else
        {
            _logger.LogDebug(
                "Vehicle reading: SOC={Soc}% charge={ChargeState} plug={PlugState} captured {CapturedAt:O}.",
                state.SocPercent, state.ChargeState, state.PlugState, state.CapturedAt);
        }

        return Task.CompletedTask;
    }

    private async Task DisconnectAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vehicle telemetry disconnect failed on shutdown.");
        }
        finally
        {
            _client.Dispose();
            _client = null;
        }
    }
}
