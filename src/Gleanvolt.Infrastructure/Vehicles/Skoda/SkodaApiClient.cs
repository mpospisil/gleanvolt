using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// The MyŠkoda Public API, as far as Gleanvolt reads it (issue #193): one <c>GET</c>.
///
/// <para><b>Read only, by construction.</b> The API also starts and stops charging, sets the charge
/// limit and changes the charge mode, with the same key. This class has no method for any of them, so
/// nothing that holds it can: #73 stands, and nothing that writes to hardware depends on a cloud feed.
/// The charger is what Gleanvolt drives.</para>
///
/// <para><b>One client for the sign-in and the feed.</b> Both spend the same hourly quota, so the
/// <c>RateLimit-*</c> headers are recorded here, on every response, whoever asked — the headers are
/// the authority, and counting locally would drift from them the first time a sign-in and a fetch
/// crossed.</para>
///
/// <para>The key is sent in <c>X-API-Key</c> and appears nowhere else: not in a log line, not in an
/// exception message, not in what this returns.</para>
/// </summary>
public sealed class SkodaApiClient : IDisposable
{
    /// <summary>The problem-type suffixes the spec names. Matched on the suffix, so the test host's are too.</summary>
    private const string KeyExpired = "/problems/api-key-expired";
    private const string KeyNotAuthorized = "/problems/api-key-not-authorized";
    private const string RateLimitExceeded = "/problems/rate-limit-exceeded";
    private const string VehicleNotAccepting = "/problems/vehicle-not-accepting-requests";

    private readonly SkodaApiOptions _options;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private volatile SkodaApiQuota? _quota;

    /// <param name="options">The base address, VIN and timeout.</param>
    /// <param name="time">The clock quota resets are placed on.</param>
    /// <param name="transport">
    /// The HTTP transport, so a test can drive the sign-in and the feed against spec-built answers and
    /// no network. Null builds the real one; a supplied one is the caller's to dispose.
    /// </param>
    public SkodaApiClient(SkodaApiOptions options, TimeProvider? time = null, HttpMessageHandler? transport = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        _http = transport is null
            ? new HttpClient { Timeout = options.Timeout }
            : new HttpClient(transport, disposeHandler: false) { Timeout = options.Timeout };
    }

    /// <summary>The quota as the last response described it, or null before the first.</summary>
    public SkodaApiQuota? Quota => _quota;

    /// <summary>
    /// <c>GET /api/v1/vehicles/{vin}?include=…</c>. Never throws for a refusal or an unreachable API:
    /// every expected failure is an <see cref="SkodaApiOutcome"/>, and only the caller's own
    /// cancellation escapes.
    /// </summary>
    /// <param name="key">The API key. Sent in the header and nowhere else.</param>
    /// <param name="include">Which parts of the vehicle to ask for: <c>info</c>, <c>charging</c>, …</param>
    public async Task<SkodaApiResponse> GetVehicleAsync(
        string key, string include, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/v1/vehicles/{Uri.EscapeDataString(_options.Vin.Trim())}"
            + $"?include={Uri.EscapeDataString(include)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-API-Key", key.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("application/problem+json");

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            var retryAfter = RetryAfter(response, now);
            var remaining = HeaderInt(response, "RateLimit-Remaining");

            if (remaining is not null)
            {
                var reset = HeaderInt(response, "RateLimit-Reset");
                _quota = new SkodaApiQuota(
                    HeaderInt(response, "RateLimit-Limit"),
                    remaining.Value,
                    reset is { } seconds ? now + TimeSpan.FromSeconds(seconds) : null);
            }

            var problem = ProblemType(body);
            var outcome = Classify(response.StatusCode, problem);

            return new SkodaApiResponse(
                outcome,
                (int)response.StatusCode,
                outcome == SkodaApiOutcome.Ok ? body : null,
                KeyExpiresAt(response),
                retryAfter,
                outcome == SkodaApiOutcome.Ok ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // TaskCanceledException without the caller cancelling is HttpClient's timeout. The message
            // is the transport's and carries the URL at most, never the header.
            var detail = ex is TaskCanceledException ? "timed out" : ex.Message;
            return new SkodaApiResponse(SkodaApiOutcome.Unavailable, 0, null, null, null, detail);
        }
    }

    private static SkodaApiOutcome Classify(HttpStatusCode status, string? problem) => (int)status switch
    {
        >= 200 and < 300 => SkodaApiOutcome.Ok,
        401 when Is(problem, KeyExpired) => SkodaApiOutcome.KeyExpired,
        401 => SkodaApiOutcome.KeyUnknown,
        // 403 without the named type is operation-not-authorized: the vehicle refusing the key's user,
        // which for a read is the same owner action -- the key does not cover this car.
        403 => SkodaApiOutcome.KeyNotAuthorized,
        404 => SkodaApiOutcome.VehicleNotFound,
        429 when Is(problem, VehicleNotAccepting) => SkodaApiOutcome.VehicleNotAccepting,
        // A 429 with no recognisable type is treated as ours: backing off is right for both.
        429 => SkodaApiOutcome.RateLimited,
        _ => SkodaApiOutcome.Unavailable,
    };

    private static bool Is(string? problem, string suffix) =>
        problem is not null && problem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The RFC 9457 <c>type</c>, when the body is a problem document.</summary>
    private static string? ProblemType(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? KeyExpiresAt(HttpResponseMessage response) =>
        Header(response, "X-API-Key-Expires-At") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary><c>Retry-After</c> in either of its two forms: seconds, or an HTTP date.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is not { } retry)
        {
            return null;
        }

        if (retry.Delta is { } delta)
        {
            return delta;
        }

        return retry.Date is { } date && date > now ? date - now : null;
    }

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        Header(response, name) is { } text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()?.Trim()
            : null;

    public void Dispose() => _http.Dispose();
}

/// <summary>What a request came to, in the terms the sign-in and the feed each need a sentence for.</summary>
public enum SkodaApiOutcome
{
    Ok,

    /// <summary>401 <c>api-key-expired</c>.</summary>
    KeyExpired,

    /// <summary>401 of any other kind: a key Škoda does not recognise.</summary>
    KeyUnknown,

    /// <summary>403: the key does not cover this VIN.</summary>
    KeyNotAuthorized,

    /// <summary>404: no car with this VIN.</summary>
    VehicleNotFound,

    /// <summary>429 <c>rate-limit-exceeded</c>: our quota, spent.</summary>
    RateLimited,

    /// <summary>429 <c>vehicle-not-accepting-requests</c>: the car, not us — a low 12 V battery or deep sleep.</summary>
    VehicleNotAccepting,

    /// <summary>5xx, a timeout, or no answer at all.</summary>
    Unavailable,
}

/// <param name="Outcome">What it came to.</param>
/// <param name="StatusCode">The HTTP status, or 0 when nothing answered.</param>
/// <param name="Body">The payload on success; null otherwise, so a problem body is never passed on.</param>
/// <param name="KeyExpiresAt">From <c>X-API-Key-Expires-At</c>, sent on every successful response.</param>
/// <param name="RetryAfter">From <c>Retry-After</c>, on a 429 or a 503.</param>
/// <param name="Detail">A short reason for a log line. Never carries the key.</param>
public sealed record SkodaApiResponse(
    SkodaApiOutcome Outcome,
    int StatusCode,
    string? Body,
    DateTimeOffset? KeyExpiresAt,
    TimeSpan? RetryAfter,
    string? Detail);

/// <summary>The hourly quota as Škoda last described it.</summary>
/// <param name="Limit">Requests per window, when the header was sent.</param>
/// <param name="Remaining">Requests left in the current window.</param>
/// <param name="ResetsAt">When the window replenishes, when the header was sent.</param>
public sealed record SkodaApiQuota(int? Limit, int Remaining, DateTimeOffset? ResetsAt);
