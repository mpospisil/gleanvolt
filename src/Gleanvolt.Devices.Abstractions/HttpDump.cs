using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gleanvolt.Devices;

/// <summary>
/// What a snapshot of a charger reached over HTTP reads (issue #352), as <see cref="RegisterSnapshotSpec"/> is
/// for Modbus: the requests, each a read, relative to the charger's base address. At least every request the
/// driver makes, so a recording replays the driver; and nothing that names the owner, because the file is
/// meant to be sent.
/// </summary>
/// <param name="Requests">The paths and queries, without a leading slash (<c>api/status?filter=car,nrg</c>).</param>
public sealed record HttpSnapshotSpec(IReadOnlyList<string> Requests);

/// <summary>
/// A recording of a charger's HTTP API (issue #352), as <see cref="RegisterDump"/> is of a Modbus device's
/// registers: what Settings → charger → "Save register snapshot" saves for a go-e, and what the replay answers
/// from in its driver's tests.
///
/// <code>
/// {
///   "format": "gleanvolt-http-dump", "formatVersion": 1,
///   "driver": "goe", "family": "go-e Charger Gemini 22",
///   "gleanvoltVersion": "1.2.40", "capturedAt": "2026-11-09T12:00:00+00:00",
///   "note": "",
///   "responses": { "api/status?filter=car,nrg": { "car": 1, "nrg": [ ... ] } }
/// }
/// </code>
///
/// <para>A request that is not in <c>responses</c> is one nobody recorded; a replay answers it 404, as a web
/// server does for a path it does not have.</para>
/// </summary>
public sealed record HttpDump
{
    /// <summary>The <c>format</c> value every HTTP dump carries.</summary>
    public const string FormatName = "gleanvolt-http-dump";

    /// <summary>The version this code writes, and the highest it reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The driver id the dump was taken with (<c>Pv:Chargers:0:Driver</c>).</summary>
    public string Driver { get; init; } = string.Empty;

    /// <summary>What the device is, as its owner configured it; free text.</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>The Gleanvolt version that took the dump.</summary>
    public string GleanvoltVersion { get; init; } = string.Empty;

    /// <summary>When the dump was taken.</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>What was happening when it was taken ("car plugged in, waiting").</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Each recorded request's response body, by path and query.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Responses { get; init; } = new Dictionary<string, JsonNode?>();

    /// <summary>The dump as the file holds it.</summary>
    public string ToJson()
    {
        var responses = new JsonObject();
        foreach (var (request, body) in Responses.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            responses[request] = body?.DeepClone();
        }

        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["formatVersion"] = CurrentFormatVersion,
            ["driver"] = Driver,
            ["family"] = Family,
            ["gleanvoltVersion"] = GleanvoltVersion,
            ["capturedAt"] = CapturedAt?.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = Note,
            ["responses"] = responses,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads an HTTP dump file.</summary>
    /// <exception cref="FormatException">The text is not an HTTP dump this version can read.</exception>
    public static HttpDump FromJson(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An HTTP dump is a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not JSON: " + ex.Message, ex);
        }

        if ((string?)root["format"] != FormatName)
        {
            throw new FormatException($"Not an HTTP dump: 'format' is not '{FormatName}'.");
        }

        if (root["formatVersion"] is not JsonValue version || version.GetValue<int>() is < 1 or > CurrentFormatVersion)
        {
            throw new FormatException($"Unsupported HTTP dump format version; this build reads up to {CurrentFormatVersion}.");
        }

        var responses = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (root["responses"] is JsonObject recorded)
        {
            foreach (var (request, body) in recorded)
            {
                responses[request.TrimStart('/')] = body?.DeepClone();
            }
        }

        return new HttpDump
        {
            Driver = (string?)root["driver"] ?? string.Empty,
            Family = (string?)root["family"] ?? string.Empty,
            GleanvoltVersion = (string?)root["gleanvoltVersion"] ?? string.Empty,
            CapturedAt = (string?)root["capturedAt"] is { Length: > 0 } captured
                ? DateTimeOffset.Parse(captured, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null,
            Note = (string?)root["note"] ?? string.Empty,
            Responses = responses,
        };
    }
}

/// <summary>Takes an <see cref="HttpDump"/>: each request of an <see cref="HttpSnapshotSpec"/>, once.</summary>
public static class HttpSnapshot
{
    /// <summary>
    /// Reads every request in <paramref name="spec"/> on <paramref name="client"/> (whose base address is the
    /// charger), each marked as a read, and returns <paramref name="header"/> with the responses. Throws when
    /// a request fails or its body is not JSON: a partial recording would replay a driver that never existed.
    /// </summary>
    public static async Task<HttpDump> TakeAsync(
        HttpClient client, HttpSnapshotSpec spec, HttpDump header, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(header);

        var responses = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var path in spec.Requests)
        {
            using var request = ChargerHttp.Read(path);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                responses[path.TrimStart('/')] = JsonNode.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"The charger's answer to {path} is not JSON.", ex);
            }
        }

        return header with { Responses = responses };
    }
}
