using System.Net;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Vehicles.Skoda;
using static Gleanvolt.Infrastructure.Tests.SkodaFixtures;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The MyŠkoda feed on its own clock (issue #193): one request per reading, inside twenty an hour, a
/// quota spent by us told apart from a car that declined, and a key problem that waits for a new key
/// rather than a restart.
/// </summary>
public sealed class SkodaApiUpdateServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 18, 40, 0, TimeSpan.Zero);

    private readonly string _keyPath = KeyPath();
    private readonly TestClock _clock = new(Now);
    private readonly FakeApi _api = new(_ => Json(HttpStatusCode.OK, "connect-cable.json", Now.AddMonths(6)));
    private readonly SkodaApiKeyStore _store;
    private readonly SkodaApiClient _client;
    private readonly SkodaApiUpdateService _service;

    public SkodaApiUpdateServiceTests()
    {
        _store = new SkodaApiKeyStore(_keyPath);
        _client = new SkodaApiClient(Options(), _clock, _api);
        _service = new SkodaApiUpdateService(Options(), _store, _client, "enyaq", time: _clock);
    }

    public void Dispose()
    {
        _client.Dispose();
        var directory = Path.GetDirectoryName(_keyPath)!;

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void PasteKey(string key = Key) => _store.Save(new SkodaApiKey(key, Now.AddMonths(6), "My Enyaq"));

    [Fact]
    public async Task With_no_key_it_needs_the_owner_and_sends_nothing()
    {
        Assert.True(_service.Health.IsBlocked);
        Assert.Contains("Vehicle portal", _service.Health.Message);

        Assert.Null(await _service.FetchAsync(CancellationToken.None));
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public void With_no_key_the_card_is_told_to_paste_one()
    {
        // #212: the dashboard's sentence, with the link to the page where the key goes.
        Assert.Equal("MyŠkoda", _service.DisplayName);
        Assert.Equal(SkodaApiSentences.OwnerAction, _service.OwnerAction);
    }

    [Fact]
    public async Task A_healthy_feed_has_no_fix_to_offer()
    {
        PasteKey();
        await _service.FetchAsync(CancellationToken.None);

        Assert.Null(_service.OwnerAction);
    }

    [Fact]
    public async Task One_request_per_reading_labelled_with_the_source()
    {
        PasteKey();

        var state = await _service.FetchAsync(CancellationToken.None);

        Assert.Equal(48, state?.SocPercent);
        Assert.Equal("skoda", state?.SourceId);
        Assert.Equal(VehicleSourceState.Ok, _service.Health.State);
        Assert.Equal("skoda", _service.Manufacturer);
        Assert.Equal("enyaq", _service.VehicleId);

        var request = Assert.Single(_api.Requests);
        Assert.Equal($"{BaseUrl}/api/v1/vehicles/{Vin}?include=charging", request.Url);
        Assert.Equal(Key, request.Key);
    }

    [Fact]
    public void It_is_on_its_own_clock_between_charges_too()
    {
        Assert.False(_service.DeliversOnlyWhileCharging);
    }

    [Fact]
    public async Task Idle_it_asks_every_fifteen_minutes_and_while_charging_every_five()
    {
        PasteKey();

        await _service.FetchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(15), _service.NextDelay);

        _api.Answer = _ => Json(HttpStatusCode.OK, "charging.json");
        _clock.Advance(TimeSpan.FromMinutes(15));
        await _service.FetchAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), _service.NextDelay);
    }

    /// <summary>The clock and an ask in the same minute share one request.</summary>
    [Fact]
    public async Task Two_asks_in_the_same_minute_spend_one_request()
    {
        PasteKey();

        var first = await _service.FetchAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(20));
        var second = await _service.FetchAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task Near_the_end_of_the_quota_the_clock_waits_for_the_window_and_leaves_the_rest_to_people()
    {
        PasteKey();
        _api.Answer = _ => Json(HttpStatusCode.OK, "connect-cable.json", remaining: 3, reset: 2400);

        await _service.FetchAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2400), _service.NextDelay);

        // An ask can still spend what is left.
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await _service.FetchAsync(CancellationToken.None));
        Assert.Equal(2, _api.Requests.Count);
    }

    [Fact]
    public async Task With_the_quota_spent_nothing_is_sent_until_it_resets()
    {
        PasteKey();
        _api.Answer = _ => Json(HttpStatusCode.OK, "connect-cable.json", remaining: 0, reset: 600);

        await _service.FetchAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(await _service.FetchAsync(CancellationToken.None));
        Assert.Single(_api.Requests);
        Assert.Contains("used up", _service.Health.Message);

        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(await _service.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rate_limit_backs_off_per_retry_after_without_dropping_the_reading()
    {
        PasteKey();
        await _service.FetchAsync(CancellationToken.None);

        _api.Answer = _ => Json(
            HttpStatusCode.TooManyRequests, "problem-rate-limit-exceeded.json", retryAfter: TimeSpan.FromMinutes(40));
        _clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Null(await _service.FetchAsync(CancellationToken.None));
        Assert.Equal(VehicleSourceState.Degraded, _service.Health.State);
        Assert.Contains("quota", _service.Health.Message);
        Assert.Contains("last reading stands", _service.Health.Message);
        Assert.True(_service.NextDelay >= TimeSpan.FromMinutes(40));

        // Honoured for an ask too: nothing is sent inside the window.
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await _service.FetchAsync(CancellationToken.None));
        Assert.Equal(2, _api.Requests.Count);
    }

    [Fact]
    public async Task A_car_that_declines_is_its_own_sentence_and_is_left_alone_longer()
    {
        PasteKey();
        _api.Answer = _ => Json(
            HttpStatusCode.TooManyRequests, "problem-vehicle-not-accepting-requests.json",
            retryAfter: TimeSpan.FromMinutes(1));

        Assert.Null(await _service.FetchAsync(CancellationToken.None));

        Assert.Equal(VehicleSourceState.Degraded, _service.Health.State);
        Assert.Contains("car itself", _service.Health.Message);
        Assert.DoesNotContain("quota", _service.Health.Message);
        Assert.True(_service.NextDelay >= SkodaApiUpdateService.VehicleDeclinedBackoff);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "problem-api-key-expired.json", "has expired")]
    [InlineData(HttpStatusCode.Unauthorized, "problem-unauthorized.json", "no longer recognises")]
    [InlineData(HttpStatusCode.Forbidden, "problem-api-key-not-authorized.json", "not allowed for VIN")]
    public async Task A_key_problem_needs_the_owner_and_is_not_retried(
        HttpStatusCode status, string fixture, string sentence)
    {
        PasteKey();
        _api.Answer = _ => Json(status, fixture);

        Assert.Null(await _service.FetchAsync(CancellationToken.None));

        Assert.True(_service.Health.IsBlocked);
        Assert.Contains(sentence, _service.Health.Message);
        Assert.Equal(Timeout.InfiniteTimeSpan, _service.NextDelay);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await _service.FetchAsync(CancellationToken.None));
        Assert.Single(_api.Requests);
    }

    /// <summary>The page writes the key into the store this reads; no restart stands between them.</summary>
    [Fact]
    public async Task A_new_key_unblocks_the_feed()
    {
        PasteKey();
        _api.Answer = _ => Json(HttpStatusCode.Unauthorized, "problem-api-key-expired.json");
        await _service.FetchAsync(CancellationToken.None);
        Assert.True(_service.Health.IsBlocked);

        PasteKey("sk-live-new");
        _api.Answer = _ => Json(HttpStatusCode.OK, "charging.json");
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(_service.Health.IsBlocked);
        Assert.Equal(71, (await _service.FetchAsync(CancellationToken.None))?.SocPercent);
        Assert.Equal("sk-live-new", _api.Requests[^1].Key);
    }

    [Fact]
    public async Task A_key_expiring_within_a_week_is_degraded_and_says_when()
    {
        _store.Save(new SkodaApiKey(Key, Now.AddDays(3), "My Enyaq"));
        _api.Answer = _ => Json(HttpStatusCode.OK, "connect-cable.json", Now.AddDays(3));

        Assert.NotNull(await _service.FetchAsync(CancellationToken.None));

        Assert.Equal(VehicleSourceState.Degraded, _service.Health.State);
        Assert.Contains("expires on 2026-09-18", _service.Health.Message);
    }

    [Fact]
    public async Task The_expiry_the_api_reports_is_kept()
    {
        PasteKey();
        var renewed = Now.AddYears(1);
        _api.Answer = _ => Json(HttpStatusCode.OK, "connect-cable.json", renewed);

        await _service.FetchAsync(CancellationToken.None);

        Assert.Equal(renewed, _store.Current?.ExpiresAt);
        Assert.Equal(renewed, new SkodaApiKeyStore(_keyPath).Current?.ExpiresAt);
    }

    [Fact]
    public async Task An_unreachable_api_degrades_and_backs_off()
    {
        PasteKey();
        _api.Answer = _ => throw new HttpRequestException("name resolution failed");

        Assert.Null(await _service.FetchAsync(CancellationToken.None));

        Assert.Equal(VehicleSourceState.Degraded, _service.Health.State);
        Assert.Contains("did not answer", _service.Health.Message);
        Assert.True(_service.NextDelay >= TimeSpan.FromMinutes(15));
    }
}
