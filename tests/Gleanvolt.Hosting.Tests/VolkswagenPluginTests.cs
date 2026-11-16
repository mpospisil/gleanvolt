using System.Net;
using System.Net.Sockets;
using System.Text;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Enums;
using Gleanvolt.Hosting.Vehicles.External;
using Gleanvolt.Plugin.Volkswagen.Volkswagen;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The Volkswagen plugin's status → <see cref="VehicleReading"/> mapping (#410), against the built-in
/// feed's real capture from the reference ID.4, and checked by Gleanvolt's own validator: what the hub
/// would do with it.
/// </summary>
public class VolkswagenPluginMappingTests
{
    private static readonly DateTimeOffset Captured = new(2026, 9, 5, 8, 39, 1, TimeSpan.Zero);

    internal static string Capture() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwWebsite", "charging-status.json"));

    [Fact]
    public void The_real_payload_maps_to_a_reading()
    {
        var reading = VwWebsiteChargingStatus.Parse(Capture(), out var error);

        Assert.Null(error);
        Assert.Equal(new VehicleReading(
            Captured,
            SocPercent: 55,
            RangeMetres: 220_000,
            ChargeTimeRemaining: TimeSpan.Zero,
            ChargeState: CarChargeState.Idle,
            PlugState: CarPlugState.Disconnected), reading);
    }

    /// <summary>The reason for the whole client: the car's own capture time, not the time it was read.</summary>
    [Fact]
    public void The_capture_time_is_the_cars_own()
    {
        Assert.Equal(Captured, VwWebsiteChargingStatus.Parse(Capture(), out _)!.CapturedAt);
    }

    [Fact]
    public void The_reading_passes_gleanvolts_validator()
    {
        var reading = VwWebsiteChargingStatus.Parse(Capture(), out _)!;

        var problems = VehicleReadingValidator.Check(VehicleHub.ToState(reading, "Volkswagen"), held: null, now: Captured.AddMinutes(10));

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("charging", CarChargeState.Charging)]
    [InlineData("readyForCharging", CarChargeState.Idle)]
    [InlineData("notReadyForCharging", CarChargeState.Idle)]
    [InlineData("conservation", CarChargeState.Idle)]
    [InlineData("chargePurposeReachedAndNotConservationCharging", CarChargeState.Complete)]
    [InlineData("chargePurposeReachedAndConservation", CarChargeState.Complete)]
    [InlineData("somethingNew", CarChargeState.Unknown)]
    public void Charge_states_map_and_an_unknown_one_costs_only_itself(string value, CarChargeState expected)
    {
        var reading = VwWebsiteChargingStatus.Parse(Payload(chargingState: value), out var error);

        Assert.Null(error);
        Assert.Equal(expected, reading!.ChargeState);
        Assert.Equal(55, reading.SocPercent);
        Assert.Empty(VehicleReadingValidator.Check(VehicleHub.ToState(reading, "Volkswagen"), null, Captured));
    }

    [Theory]
    [InlineData("connected", CarPlugState.Connected)]
    [InlineData("disconnected", CarPlugState.Disconnected)]
    [InlineData("invalid", CarPlugState.Unknown)]
    public void Plug_states_map(string value, CarPlugState expected)
    {
        Assert.Equal(expected, VwWebsiteChargingStatus.Parse(Payload(plug: value), out _)!.PlugState);
    }

    /// <summary>
    /// The built-in kept up to 7 days; the contract refuses a whole reading over 48 hours, so the plugin
    /// leaves the time out and sends the rest.
    /// </summary>
    [Fact]
    public void A_remaining_time_over_48_hours_is_left_out_and_the_reading_still_passes()
    {
        var reading = VwWebsiteChargingStatus.Parse(Payload(remainingMinutes: 49 * 60), out _)!;

        Assert.Null(reading.ChargeTimeRemaining);
        Assert.Equal(55, reading.SocPercent);
        Assert.Empty(VehicleReadingValidator.Check(VehicleHub.ToState(reading, "Volkswagen"), null, Captured));
        Assert.Equal(TimeSpan.FromHours(48), VwWebsiteChargingStatus.Parse(Payload(remainingMinutes: 48 * 60), out _)!.ChargeTimeRemaining);
    }

    [Fact]
    public void A_charging_reading_with_time_to_full_passes()
    {
        var reading = VwWebsiteChargingStatus.Parse(Payload(chargingState: "charging", plug: "connected", remainingMinutes: 95), out _)!;

        Assert.Equal(TimeSpan.FromMinutes(95), reading.ChargeTimeRemaining);
        Assert.Equal(CarPlugState.Connected, reading.PlugState);
        Assert.Empty(VehicleReadingValidator.Check(VehicleHub.ToState(reading, "Volkswagen"), null, Captured));
    }

    /// <summary>#73's rule: a body we cannot believe is refused whole rather than half-trusted.</summary>
    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("not json", "not JSON")]
    [InlineData("""{"nothing":1}""", "no 'data'")]
    [InlineData("""{"data":{"batteryStatus":{"currentSOC_pct":55}}}""", "carCapturedTimestamp")]
    public void An_unusable_body_is_refused_with_a_reason(string? json, string expected)
    {
        Assert.Null(VwWebsiteChargingStatus.Parse(json, out var error));
        Assert.Contains(expected, error);
    }

    private static string Payload(string chargingState = "notReadyForCharging", string plug = "disconnected", int remainingMinutes = 0) =>
        "{\"data\":{\"batteryStatus\":{\"carCapturedTimestamp\":\"2026-09-05T08:39:01Z\",\"currentSOC_pct\":55,\"cruisingRangeElectric_km\":220},"
        + "\"chargingStatus\":{\"carCapturedTimestamp\":\"2026-09-05T08:39:01Z\",\"chargingState\":\"" + chargingState + "\","
        + "\"remainingChargingTimeToComplete_min\":" + remainingMinutes + "},"
        + "\"plugStatus\":{\"carCapturedTimestamp\":\"2026-09-05T08:39:01Z\",\"plugConnectionState\":\"" + plug + "\"}}}";
}

/// <summary>
/// The plugin's copy of the built-in's lockout rule (#410): read on the car's clock, stopped for the
/// owner on any failure, nothing sent while stopped, and back on the clock after a sign-in on the page.
///
/// <para>A loopback socket stands in for volkswagen.de, as in the built-in's tests: every request is
/// answered 200, which the login reads as signed in, and <c>charging/status</c> with the capture or,
/// when <see cref="_answering"/> is off, with nothing usable.</para>
/// </summary>
public sealed class VolkswagenPluginFeedTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"gleanvolt-vw-plugin-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly VolkswagenOptions _options;
    private readonly AccountStore _account;
    private readonly VwWebsiteClient _client;
    private readonly VolkswagenFeed _feed;
    private volatile bool _answering = true;
    private volatile string _chargingState = "notReadyForCharging";
    private int _requests;

    public VolkswagenPluginFeedTests()
    {
        _listener.Start();
        _ = ServeAsync();

        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _options = new VolkswagenOptions
        {
            PortalBaseUrl = baseUrl,
            IdentityBaseUrl = baseUrl,
            DataDirectory = _directory,
            Timeout = TimeSpan.FromSeconds(5),
        };

        var files = new DataFiles(_options);
        _account = new AccountStore(files);
        Assert.True(_account.Save("owner@example.com", "hunter2", "wvgzzze2zpe999999"));
        _client = new VwWebsiteClient(_options, _account, new VwWebsiteSessionStore(files), time: _time);
        _feed = new VolkswagenFeed(_options, _account, _client, _time, NullLogger<VolkswagenFeed>.Instance);

        // A first save is a new account: the feed waits for a sign-in, as the page asks.
        _feed.Resume();
    }

    private int Requests => Volatile.Read(ref _requests);

    public void Dispose()
    {
        _listener.Stop();
        _client.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_parked_car_is_read_on_the_idle_clock()
    {
        var reading = await _feed.ReadAsync(CancellationToken.None);

        Assert.Equal(55, reading!.SocPercent);
        Assert.True(_feed.Status!.Healthy);
        Assert.Equal(_options.IdlePollInterval, _feed.NextDelay);
    }

    /// <summary>The built-in asks whether Gleanvolt has a session open; the plugin asks the car.</summary>
    [Fact]
    public async Task A_car_that_says_it_is_charging_is_read_on_the_charging_clock()
    {
        _chargingState = "charging";

        await _feed.ReadAsync(CancellationToken.None);

        Assert.Equal(_options.PollInterval, _feed.NextDelay);
    }

    [Fact]
    public async Task No_usable_answer_stops_the_feed_and_needs_the_owner()
    {
        _answering = false;

        Assert.Null(await _feed.ReadAsync(CancellationToken.None));

        Assert.True(_feed.IsBlocked);
        Assert.Equal(new { Healthy = false, NeedsOwner = true }, new { _feed.Status!.Healthy, _feed.Status.NeedsOwner });
        Assert.Contains("Sign in again on the plugin's page", _feed.Status.Message);
        Assert.Equal(Timeout.InfiniteTimeSpan, _feed.NextDelay);
    }

    [Fact]
    public async Task Once_stopped_nothing_is_sent_and_the_last_reading_answers()
    {
        var good = await _feed.ReadAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(15));
        _answering = false;
        await _feed.ReadAsync(CancellationToken.None);
        var sent = Requests;

        _answering = true;
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(good, await _feed.ReadAsync(CancellationToken.None));
        Assert.Equal(sent, Requests);
    }

    [Fact]
    public async Task Signing_in_on_the_page_puts_the_feed_back_on_its_clock_without_a_restart()
    {
        _answering = false;
        await _feed.ReadAsync(CancellationToken.None);
        Assert.True(_feed.IsBlocked);

        // What the page's Sign in does.
        Assert.Equal(VwWebsiteLoginStep.SignedIn, await _client.SignInAsync());
        _feed.Resume();

        Assert.False(_feed.IsBlocked);
        Assert.True(_feed.Status!.Healthy);
        _answering = true;
        Assert.NotNull(await _feed.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_volkswagen_de_stops_the_feed_too()
    {
        _listener.Stop();

        Assert.Null(await _feed.ReadAsync(CancellationToken.None));

        Assert.True(_feed.Status!.NeedsOwner);
        Assert.Contains("unreachable", _feed.Status.Message);
    }

    [Fact]
    public async Task Forgetting_the_session_needs_the_owner_and_sends_nothing()
    {
        await _feed.ReadAsync(CancellationToken.None);
        var sent = Requests;

        _feed.Stop("You forgot the session.");
        _time.Advance(TimeSpan.FromHours(1));
        await _feed.ReadAsync(CancellationToken.None);

        Assert.True(_feed.Status!.NeedsOwner);
        Assert.Equal(sent, Requests);
    }

    [Fact]
    public async Task Asking_again_within_the_minimum_gap_answers_with_the_last_reading()
    {
        await _feed.ReadAsync(CancellationToken.None);
        var sent = Requests;

        _time.Advance(_options.MinimumTimeBetweenReads - TimeSpan.FromSeconds(1));
        Assert.NotNull(await _feed.ReadAsync(CancellationToken.None));
        Assert.Equal(sent, Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        await _feed.ReadAsync(CancellationToken.None);
        Assert.True(Requests > sent);
    }

    [Fact]
    public void Without_an_account_the_owner_is_needed()
    {
        var files = new DataFiles(new VolkswagenOptions { DataDirectory = Path.Combine(_directory, "empty") });
        var account = new AccountStore(files);
        using var client = new VwWebsiteClient(_options, account, new VwWebsiteSessionStore(files));
        var feed = new VolkswagenFeed(_options, account, client, _time, NullLogger<VolkswagenFeed>.Instance);

        Assert.True(feed.IsBlocked);
        Assert.True(feed.Status!.NeedsOwner);
        Assert.Contains("not entered yet", feed.Status.Message);
    }

    [Fact]
    public async Task Another_account_stops_the_feed_until_the_owner_signs_in_but_a_new_password_does_not()
    {
        await _feed.ReadAsync(CancellationToken.None);

        Assert.True(_account.Save("owner@example.com", "a-new-password", "WVGZZZE2ZPE999999"));
        Assert.False(_feed.IsBlocked);

        Assert.True(_account.Save("owner@example.com", null, "WVGZZZE2ZPE000001"));
        Assert.True(_feed.IsBlocked);
        Assert.Equal("a-new-password", _account.Current.Password);
    }

    [Fact]
    public void The_account_and_the_session_are_readable_only_by_the_plugins_user()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(Path.Combine(_directory, "account.json"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        Assert.DoesNotContain("hunter2", _account.Current.ToString());
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
                    ? (_answering ? VolkswagenPluginMappingTests.Capture().Replace("notReadyForCharging", _chargingState, StringComparison.Ordinal) : "not a reading")
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
}
