using System.Net;
using System.Net.Sockets;
using System.Text;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Vehicles.VwWebsite;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// volkswagen.de as the car's only feed (issues #170, #212): read continuously, stopped for the owner
/// on any failure, and back on its clock once the owner has signed in again.
///
/// <para>A loopback socket stands in for volkswagen.de. It answers every request <c>200</c>, which the
/// login flow reads as signed in, and answers <c>charging/status</c> with the captured payload — or,
/// when <see cref="_answering"/> is off, with nothing usable.</para>
/// </summary>
public sealed class VwWebsiteUpdateServiceTests : IDisposable
{
    private static readonly string Capture = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwWebsite", "charging-status.json"));

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _sessionPath =
        Path.Combine(Path.GetTempPath(), $"gleanvolt-vw-website-{Guid.NewGuid():N}", "session.json");
    private readonly VwWebsiteOptions _options;
    private readonly VwWebsiteClient _client;
    private readonly VwWebsiteUpdateService _service;
    private volatile bool _answering = true;
    private int _requests;

    public VwWebsiteUpdateServiceTests()
    {
        _listener.Start();
        _ = ServeAsync();

        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _options = new VwWebsiteOptions
        {
            Enabled = true,
            Username = "owner@example.com",
            Password = "hunter2",
            Vin = "WVGZZZE2ZPE999999",
            PortalBaseUrl = baseUrl,
            IdentityBaseUrl = baseUrl,
            Timeout = TimeSpan.FromSeconds(5),
        };

        _client = new VwWebsiteClient(_options, new VwWebsiteSessionStore(_sessionPath));

        // A holder that has never seen a poll: nothing is charging, the car is parked.
        _service = new VwWebsiteUpdateService(_options, _client, new ChargeControlStatusHolder(), "id4");
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var socket = await _listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var buffer = new byte[16384];
                var read = await stream.ReadAsync(buffer);
                var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0];
                Interlocked.Increment(ref _requests);

                var body = requestLine.Contains("charging/status", StringComparison.Ordinal)
                    ? (_answering ? Capture : "not a reading")
                    : "<html><body>My Volkswagen</body></html>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n"
                    + "Connection: close\r\n\r\n");

                await stream.WriteAsync(head);
                await stream.WriteAsync(bytes);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            // Stopped.
        }
    }

    private int Requests => Volatile.Read(ref _requests);

    public void Dispose()
    {
        _listener.Stop();
        _client.Dispose();

        var directory = Path.GetDirectoryName(_sessionPath)!;

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_parked_car_is_read_on_the_idle_clock()
    {
        // #212: the car's only feed, so the SOC a plan starts from has to come from here.
        var state = await _service.FetchAsync(CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(VehicleSourceState.Ok, _service.Health.State);
        Assert.Equal(_options.IdlePollInterval, _service.NextDelay);
        Assert.Null(_service.OwnerAction);
    }

    [Fact]
    public async Task No_usable_answer_stops_the_feed_and_asks_the_owner_to_sign_in_again()
    {
        _answering = false;

        Assert.Null(await _service.FetchAsync(CancellationToken.None));

        Assert.True(_service.Health.IsBlocked);
        Assert.Contains("Sign in again", _service.Health.Message);
        Assert.Equal(VwWebsiteUpdateService.OwnerActionSentence, _service.OwnerAction);
        Assert.Equal(Timeout.InfiniteTimeSpan, _service.NextDelay);
    }

    [Fact]
    public async Task Once_stopped_nothing_is_sent()
    {
        _answering = false;
        await _service.FetchAsync(CancellationToken.None);
        var sent = Requests;

        _answering = true;
        Assert.Null(await _service.FetchAsync(CancellationToken.None));

        Assert.Equal(sent, Requests);
    }

    [Fact]
    public async Task Signing_in_again_puts_the_feed_back_on_its_clock_without_a_restart()
    {
        _answering = false;
        await _service.FetchAsync(CancellationToken.None);
        Assert.True(_service.Health.IsBlocked);

        // What the Vehicle portal page's Sign in does.
        Assert.Equal(VwWebsiteLoginStep.SignedIn, await _client.SignInAsync());

        Assert.False(_service.Health.IsBlocked);
        Assert.Equal(_options.IdlePollInterval, _service.NextDelay);

        _answering = true;
        Assert.NotNull(await _service.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_volkswagen_de_stops_the_feed_too()
    {
        _listener.Stop();

        Assert.Null(await _service.FetchAsync(CancellationToken.None));

        Assert.True(_service.Health.IsBlocked);
        Assert.Contains("unreachable", _service.Health.Message);
    }

    [Fact]
    public void It_is_named_in_the_owner_s_words()
    {
        Assert.Equal("volkswagen.de", _service.DisplayName);
        Assert.Equal("vw-website", _service.Manufacturer);
    }
}
