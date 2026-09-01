using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// One report snapshot out of a downloaded bundle: when the <b>car</b> produced it, and every field
/// it carried, flattened to name → value.
/// </summary>
/// <param name="CapturedAt">
/// The dataset's own timestamp, with its offset intact. Never the download time — see
/// <see cref="VwGroupReportBundle"/> for why a snapshot without one is dropped rather than dated.
/// </param>
/// <param name="Values">
/// Field name → raw value, exactly as the portal spelled them. Interpretation is
/// <see cref="VwGroupVehicleStateMapper"/>'s job; this type knows no vocabulary at all, which is what
/// lets the next manufacturer reuse the structure while sharing none of the fields.
/// </param>
/// <param name="Source">Which member of the ZIP it came from, for diagnostics.</param>
public sealed record VwGroupSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, string> Values,
    string Source);

/// <summary>
/// Turns a downloaded ZIP into report snapshots (issue #139, step 5's input).
///
/// <para>Pure and static: bytes in, snapshots out, no HTTP anywhere near it — the discipline
/// <see cref="VehicleTelemetryPayload"/> already follows, and the reason that parser has stayed
/// debuggable. The tests run against committed fixtures and never touch a network.</para>
///
/// <para><b>A download holds several snapshots, not one</b>, which is the whole reason this type
/// exists. They are returned oldest-first so the tie-break rules downstream can say "last occurrence"
/// and mean it.</para>
///
/// <para><b>Two layouts, one output.</b> The ID.x / MEB export names fields in dotted paths
/// (<c>battery.stateOfChargeInPercent</c>); older PHEV exports name them flat
/// (<c>stateOfChargeInPercent</c>). Both are read into the same dictionary with the name the portal
/// used, and the mapper matches on the leaf — which is what makes one vocabulary serve both.</para>
/// </summary>
public static class VwGroupReportBundle
{
    /// <summary>The array every report hangs its readings off.</summary>
    public const string DataProperty = "Data";

    /// <summary>Where a reading's name lives, in preference order.</summary>
    public static readonly string[] FieldNameProperties = ["dataFieldName", "key", "name"];

    /// <summary>Where a reading's value lives.</summary>
    public static readonly string[] ValueProperties = ["value", "val"];

    /// <summary>
    /// Where a snapshot's own timestamp lives, on the report object or among its readings. Several
    /// spellings because the two layouts do not agree and neither is ours to fix.
    /// </summary>
    public static readonly string[] TimestampProperties =
    [
        // car_captured_time is what a real ID.4 bundle carries, and it is the car's clock rather
        // than the portal's -- which is the whole point of CapturedAt.
        "car_captured_time", "car_captured_utc_timestamp",
        "capturedAt", "captured_at", "timestamp", "collectedAt", "collected_at",
        "createdAt", "created_at", "measurementTime", "recordDate",
    ];

    /// <summary>
    /// Reads every snapshot in the bundle, oldest first.
    /// </summary>
    /// <param name="archive">The downloaded bytes.</param>
    /// <param name="snapshots">What was found, or empty when this returns false.</param>
    /// <param name="error">Why nothing usable came back, or null on success.</param>
    public static bool TryRead(
        byte[]? archive, out IReadOnlyList<VwGroupSnapshot> snapshots, out string? error)
    {
        snapshots = [];

        if (archive is null || archive.Length == 0)
        {
            error = "the download was empty";
            return false;
        }

        using var stream = new MemoryStream(archive, writable: false);
        ZipArchive zip;

        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            // Almost always a bounce to /login or an error page arriving where a ZIP was expected.
            // Saying so beats a stack trace about a bad central directory.
            error = $"the download was not a ZIP ({ex.Message})";
            return false;
        }

        var found = new List<VwGroupSnapshot>();

        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    // Manifests, CSVs, PDFs. Not an error -- the bundle is a delivery, not a payload.
                    continue;
                }

                found.AddRange(ReadEntry(entry));
            }
        }

        if (found.Count == 0)
        {
            error = "the bundle held no dated report with any readings in it";
            return false;
        }

        snapshots = [.. found.OrderBy(snapshot => snapshot.CapturedAt)];
        error = null;
        return true;
    }

    private static IEnumerable<VwGroupSnapshot> ReadEntry(ZipArchiveEntry entry)
    {
        JsonDocument document;

        try
        {
            using var content = entry.Open();
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            // One unreadable member must not cost the bundle: a delivery carries several snapshots
            // precisely so that any of them can be the good one.
            yield break;
        }

        using (document)
        {
            foreach (var report in FindReports(document.RootElement))
            {
                var values = ReadValues(report);

                if (values.Count == 0)
                {
                    continue;
                }

                if (!TryReadTimestamp(report, values, out var capturedAt))
                {
                    // Undated, and therefore unusable: CapturedAt is the *car's* capture time, and
                    // substituting the download time would silently make every stale reading look
                    // fresh -- which is the one failure this whole feed exists to make visible.
                    continue;
                }

                yield return new VwGroupSnapshot(capturedAt, values, entry.FullName);
            }
        }
    }

    /// <summary>
    /// Every object in the document that carries a <c>Data</c> array, wherever it sits.
    ///
    /// <para>Searched rather than addressed by path: a bundle has been seen both as a file per
    /// snapshot and as one file holding an array of them, and a client that insisted on one shape
    /// would break on the other for no reason worth defending.</para>
    /// </summary>
    private static IEnumerable<JsonElement> FindReports(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty(DataProperty, out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    yield return element;
                    yield break;
                }

                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in FindReports(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in FindReports(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static Dictionary<string, string> ReadValues(JsonElement report)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reading in report.GetProperty(DataProperty).EnumerateArray())
        {
            if (reading.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = First(reading, FieldNameProperties);

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = First(reading, ValueProperties);

            if (value is null)
            {
                continue;
            }

            // Last occurrence wins inside one snapshot, which is the default rule and the one that
            // needs no special case: a report that repeats a field is amending it.
            values[name] = value;
        }

        return values;
    }

    private static bool TryReadTimestamp(
        JsonElement report, IReadOnlyDictionary<string, string> values, out DateTimeOffset capturedAt)
    {
        foreach (var candidate in TimestampProperties)
        {
            var text = report.TryGetProperty(candidate, out var property) ? Scalar(property) : null;
            text ??= values.GetValueOrDefault(candidate);

            if (!string.IsNullOrWhiteSpace(text)
                && DateTimeOffset.TryParse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out capturedAt))
            {
                return true;
            }
        }

        capturedAt = default;
        return false;
    }

    private static string? First(JsonElement element, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (element.TryGetProperty(candidate, out var property) && Scalar(property) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    // Everything is normalised to a string: the portal sends the same field as a number in one layout
    // and a quoted number in the other, and one representation downstream is worth more than
    // preserving that distinction.
    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
