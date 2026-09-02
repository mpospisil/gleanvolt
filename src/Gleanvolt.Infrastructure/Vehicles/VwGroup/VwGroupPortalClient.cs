using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>A car the account can see, and the data request that datasets hang off.</summary>
/// <param name="Vin">The VIN. Identifies the car and, by extension, its owner — so it is masked in logs.</param>
/// <param name="RequestId">
/// The continuous data request. It is created by the owner in the portal by hand and cannot be made
/// from here, which is why an account with none produces
/// <see cref="VwGroupFailure.NoDataAvailable"/> rather than an error that suggests a bug.
/// </param>
public sealed record VwGroupVehicle(string Vin, string RequestId)
{
    /// <summary>The last four characters, which is enough to tell two cars apart and identifies nobody.</summary>
    public string MaskedVin => Vin.Length > 4 ? $"…{Vin[^4..]}" : "…";
}

/// <summary>
/// The second of the two classes that touch the network: find the car, find the data, fetch it
/// (issue #139, steps 2–4), and hand the bytes to the pure mapper.
///
/// <para>Nothing here knows it is running inside a controller — no hosted service, no configuration
/// binding, no dashboard. Those belong to whoever holds one of these: the on-demand
/// <see cref="VwGroupPortalReader"/> or the <see cref="VwGroupUpdateService"/> feed. What this offers
/// is one call that produces a <see cref="VehicleState"/> and a set of failures a caller can act on
/// without reading a message.</para>
///
/// <para><b>The portal is a batch delivery, not a live API.</b> Datasets appear about every fifteen
/// minutes and only when the owner has enabled a continuous data request by hand; polling faster
/// achieves nothing at all. How often to ask is deliberately not decided here — it belongs to
/// <see cref="VwGroupUpdateService"/>, because a second manufacturer's answer would be different and
/// neither of them is a fact about this transport.</para>
/// </summary>
public sealed class VwGroupPortalClient
{
    // viewPosition is mandatory: without it the portal answers 400 "Required request parameter
    // 'viewPosition' ... is not present". It selects which photo of the car comes back, which nothing
    // here wants -- but the endpoint will not answer without one.
    private const string VehiclesPath = "/proxy_api/consent/me/vehicles?viewPosition=FRONT_LEFT";

    /// <summary>Where a VIN might be spelled, across the two layouts.</summary>
    private static readonly string[] VinProperties = ["vin", "vehicleIdentificationNumber", "vehicleId"];

    /// <summary>Where the data request's id might be spelled.</summary>
    private static readonly string[] RequestIdProperties =
        ["requestId", "dataRequestId", "consentId", "id"];

    /// <summary>Where a dataset's download link might be spelled.</summary>
    private static readonly string[] DownloadProperties = ["downloadUrl", "url", "href", "link"];

    private readonly HttpClient _http;
    private readonly VwGroupSignIn _signIn;
    private readonly VwGroupPortalOptions _options;
    private readonly ILogger _logger;

    public VwGroupPortalClient(
        HttpClient http, VwGroupPortalOptions options, VwGroupSignIn? signIn = null, ILogger? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _signIn = signIn ?? new VwGroupSignIn(http, options, _logger);
    }

    /// <summary>
    /// Raised each time this client has signed in — the first time, and again whenever the portal
    /// bounced an established session.
    ///
    /// <para><b>It exists to be measured.</b> Nobody knows how long a portal session lives, because
    /// until #140 nothing ever kept one: the on-demand reader discards its cookie jar on every press.
    /// A caller that holds one client across many fetches learns the answer by timing the gaps between
    /// these, so the instrumentation is built in rather than added after the question is asked again.
    /// The figure is a lower bound — we find out a session died only when we next use it.</para>
    ///
    /// <para>No clock here on purpose: this class has none and does not want one. It says
    /// <i>that</i> it signed in; whoever cares what time it is timestamps it.</para>
    /// </summary>
    public event Action? SignedIn;

    /// <summary>
    /// The whole of #139 in one call: sign in if the session is gone, find the car, take the newest
    /// dataset, and map it.
    ///
    /// <para>Signs in <b>on demand rather than on a schedule</b>: the session's real lifetime is what
    /// the Phase 0 spike exists to measure, and until it has, "sign in when bounced" is the only
    /// policy that is right whatever the answer turns out to be. Exactly one re-sign-in per call, so a
    /// refused password cannot become a loop.</para>
    /// </summary>
    public async Task<VwGroupMappingResult> GetVehicleStateAsync(CancellationToken cancellationToken = default)
    {
        var vehicle = await GetVehicleAsync(cancellationToken).ConfigureAwait(false);
        var (requestId, name) = await GetNewestDatasetAsync(vehicle, cancellationToken).ConfigureAwait(false);
        var archive = await DownloadAsync(vehicle.Vin, requestId, name, cancellationToken).ConfigureAwait(false);

        if (!VwGroupReportBundle.TryRead(archive, out var snapshots, out var error))
        {
            // #73's rule: present-but-unusable is a rejection, so the holder keeps its last good
            // reading and its age visibly grows -- a diagnosable state rather than half-trusted junk.
            throw new VwGroupPortalException(VwGroupFailure.UnusableData, error!);
        }

        var result = VwGroupVehicleStateMapper.Map(snapshots, vehicle.MaskedVin);

        if (result.State is null)
        {
            throw new VwGroupPortalException(VwGroupFailure.UnusableData, result.Error!);
        }

        if (result.UnmappedFields.Count > 0)
        {
            // Not a failure, and worth one line: the portal's vocabulary was written down from a
            // description rather than a capture, and this is how the gap announces itself.
            _logger.LogDebug(
                "The VW portal sent {Count} field(s) nothing here reads: {Fields}.",
                result.UnmappedFields.Count, string.Join(", ", result.UnmappedFields));
        }

        return result;
    }

    /// <summary>
    /// The identifier of this car's continuous data request, which every delivery URL hangs off.
    ///
    /// <para><b>Its own endpoint.</b> The vehicle list does not carry it — reading it from there
    /// produced an empty identifier and so "no data request" for a car that had had one since
    /// August. A 404, or a response without an <c>Identifier</c>, is what "none exists" really looks
    /// like.</para>
    /// </summary>
    public async Task<string> GetDataRequestIdAsync(string vin, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.PortalBaseUrl.TrimEnd('/')}/proxy_api/euda-apim/datarequest/vehicles/"
            + $"{Uri.EscapeDataString(vin)}/metadata/partial";

        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            root = root.GetArrayLength() > 0 ? root[0] : default;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Identifier", out var identifier)
            && identifier.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw new VwGroupPortalException(
            VwGroupFailure.NoDataRequest,
            $"vehicle ...{vin[^4..]} has no continuous data request. Create one in the portal: "
            + "Data clusters -> Vehicle overview -> Get customised data, \"All data\", every 15 "
            + "minutes. Only the owner can, in a browser, and it can take hours before the first "
            + "dataset appears.");
    }

    /// <summary>The car this client is for: the configured VIN, or the only one on the account.</summary>
    public async Task<VwGroupVehicle> GetVehicleAsync(CancellationToken cancellationToken = default)
    {
        var vehicles = await GetVehiclesAsync(cancellationToken).ConfigureAwait(false);

        if (vehicles.Count == 0)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.VehicleNotFound, "the account can see no vehicles");
        }

        if (string.IsNullOrWhiteSpace(_options.Vin))
        {
            if (vehicles.Count > 1)
            {
                // Picking for the owner would be a coin toss that looks like a decision.
                throw new VwGroupPortalException(
                    VwGroupFailure.VehicleNotFound,
                    $"the account can see {vehicles.Count} vehicles ("
                    + $"{string.Join(", ", vehicles.Select(v => v.MaskedVin))}) and none was configured");
            }

            return vehicles[0];
        }

        return vehicles.FirstOrDefault(vehicle =>
                   string.Equals(vehicle.Vin, _options.Vin, StringComparison.OrdinalIgnoreCase))
               ?? throw new VwGroupPortalException(
                   VwGroupFailure.VehicleNotFound,
                   "the configured VIN is not one this account can see");
    }

    /// <summary>Every car on the account, with the data request each one's datasets hang off.</summary>
    public async Task<IReadOnlyList<VwGroupVehicle>> GetVehiclesAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            _options.PortalBaseUrl.TrimEnd('/') + VehiclesPath, cancellationToken).ConfigureAwait(false);

        var vehicles = new List<VwGroupVehicle>();

        foreach (var element in Objects(document.RootElement))
        {
            if (Text(element, VinProperties) is not { } vin || string.IsNullOrWhiteSpace(vin))
            {
                continue;
            }

            vehicles.Add(new VwGroupVehicle(vin, Text(element, RequestIdProperties) ?? string.Empty));
        }

        return vehicles;
    }

    /// <summary>
    /// The newest dataset's <b>name</b> for this car, and the data request it belongs to.
    ///
    /// <para>A name, not a link: the list returns <c>{name, createdOn, size}</c> objects and no URL
    /// at all. The download is a separate call that takes the name as a request header.</para>
    /// </summary>
    public async Task<(string RequestId, string Name)> GetNewestDatasetAsync(
        VwGroupVehicle vehicle, CancellationToken cancellationToken = default)
    {
        var requestId = string.IsNullOrWhiteSpace(vehicle.RequestId)
            ? await GetDataRequestIdAsync(vehicle.Vin, cancellationToken).ConfigureAwait(false)
            : vehicle.RequestId;

        var url = $"{_options.PortalBaseUrl.TrimEnd('/')}/proxy_api/euda-apim/datadelivery/vehicles/"
            + $"{Uri.EscapeDataString(vehicle.Vin)}/{Uri.EscapeDataString(requestId)}/list";

        using var document = await GetJsonAsync(url, cancellationToken, PartialHeaders)
            .ConfigureAwait(false);

        string? newest = null;
        string? newestAt = null;

        // Listed newest-first by the portal, but ordering is not something to inherit on trust when a
        // wrong choice silently ages every reading. createdOn is ISO-8601 and sorts as text.
        foreach (var element in Objects(document.RootElement))
        {
            if (Text(element, ["name"]) is not { Length: > 0 } name)
            {
                continue;
            }

            var createdAt = Text(element, ["createdOn", "created_on", "createdAt"]) ?? string.Empty;

            if (newest is null || string.CompareOrdinal(createdAt, newestAt) > 0)
            {
                newest = name;
                newestAt = createdAt;
            }
        }

        if (newest is null)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.NoDataAvailable,
                $"vehicle {vehicle.MaskedVin} has a data request but no dataset to download yet");
        }

        return (requestId, newest);
    }

    /// <summary>The bundle itself. Bytes, because what to make of them is the pure mapper's business.</summary>
    public async Task<byte[]> DownloadAsync(
        string vin, string requestId, string name, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.PortalBaseUrl.TrimEnd('/')}/proxy_api/euda-apim/datadelivery/vehicles/"
            + $"{Uri.EscapeDataString(vin)}/{Uri.EscapeDataString(requestId)}/download";

        // The dataset is chosen by header, not by path or query -- the URL is the same for every one.
        var headers = new Dictionary<string, string>(PartialHeaders) { ["filename"] = name };

        var response = await SendAsync(url, cancellationToken, headers).ConfigureAwait(false);

        using (response)
        {
            Classify(response, url);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What the delivery endpoints want on every call. "partial" is the only value in use.</summary>
    private static readonly Dictionary<string, string> PartialHeaders = new() { ["type"] = "partial" };

    // One re-sign-in per call, never a loop: a refused password must fail rather than hammer the
    // account, and a session that will not stick is a fault to report rather than to work around.
    private async Task<JsonDocument> GetJsonAsync(
        string url, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await SendAsync(url, cancellationToken, headers).ConfigureAwait(false);

        if (IsSessionGone(response, url))
        {
            response.Dispose();
            _logger.LogInformation("The VW portal bounced us; signing in again.");

            await _signIn.SignInAsync(cancellationToken).ConfigureAwait(false);
            SignedIn?.Invoke();
            response = await SendAsync(url, cancellationToken, headers).ConfigureAwait(false);
        }

        using (response)
        {
            Classify(response, url);

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new VwGroupPortalException(
                    VwGroupFailure.UnusableData,
                    $"{VwGroupSignIn.Where(url)} answered with something that is not JSON ({ex.Message})", ex);
            }
        }
    }

    /// <summary>
    /// The three shapes an expired session takes, which the Phase 0 spike (#138) was built to tell
    /// apart: a 401, a bounce to <c>/login</c>, and HTML where JSON was expected. They mean the same
    /// thing here and are still worth distinguishing in the log.
    /// </summary>
    private static bool IsSessionGone(HttpResponseMessage response, string requested)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        var landed = response.RequestMessage?.RequestUri?.AbsolutePath ?? requested;

        if (landed.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        return contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
    }

    private static void Classify(HttpResponseMessage response, string url)
    {
        if (IsSessionGone(response, url))
        {
            throw new VwGroupPortalException(
                VwGroupFailure.SessionExpired,
                $"{VwGroupSignIn.Where(url)} answered {(int)response.StatusCode} and the session is gone");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.Transient,
                $"{VwGroupSignIn.Where(url)} answered {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.UnusableData,
                $"{VwGroupSignIn.Where(url)} answered {(int)response.StatusCode}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");

            // The delivery endpoints take their arguments as headers rather than query parameters --
            // "type: partial" on the list, and the dataset's own name as "filename" on the download.
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.Transient,
                $"{VwGroupSignIn.Where(url)} did not answer within {_options.Timeout}");
        }
        catch (HttpRequestException ex)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.Transient, $"could not reach {VwGroupSignIn.Where(url)} ({ex.Message})", ex);
        }
    }

    // The portal wraps its collections differently in different places -- a bare array here, an
    // { items: [...] } there. Walking for objects is shorter than keeping a table of wrappers, and it
    // does not break when a new one appears.
    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Objects(item))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Object:
                yield return element;

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        foreach (var nested in Objects(property.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
        }
    }

    private static string? Text(JsonElement element, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (element.TryGetProperty(candidate, out var property))
            {
                switch (property.ValueKind)
                {
                    case JsonValueKind.String:
                        return property.GetString();
                    case JsonValueKind.Number:
                        return property.GetRawText();
                }
            }
        }

        return null;
    }
}
