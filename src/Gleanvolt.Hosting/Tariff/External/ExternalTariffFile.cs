using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Hosting.Tariff.External;

/// <summary>
/// The external tariff's last accepted windows, kept beside <c>tariff.json</c> so a restart of
/// Gleanvolt does not lose them (#401). Without it, a restarted controller would have no tariff
/// between starting and the provider's next publish, and no tariff means no restriction: a
/// <i>low tariff only</i> charge could run at the high price in that gap.
///
/// <para>Restored once, when the strategy is made, before anything can read it. A restored tariff is
/// checked again by <see cref="ExternalTariffRules"/> against the current time and currency, and one
/// whose windows have all ended is left out. Written after every accepted publish, atomically. A file
/// that can't be read or written is logged and costs only the restore.</para>
/// </summary>
public sealed class ExternalTariffFile(string path, ILogger<ExternalTariffFile> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Where the windows are kept.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// Puts the saved windows into <paramref name="tariff"/> if they still apply, and from then on saves
    /// every publish.
    /// </summary>
    public void Attach(ExternalTariffProvider tariff, string currency, DateTimeOffset now)
    {
        if (Load(currency, now) is { } saved)
        {
            tariff.Publish(saved.Windows, saved.Origin);
            logger.LogInformation(
                "Restored the external tariff from {Path}: {Count} windows from {Provider}, received {ReceivedAt:yyyy-MM-dd HH:mm}Z.",
                Path, saved.Windows.Count, saved.Origin.Name, saved.Origin.ReceivedAt.UtcDateTime);
        }

        tariff.Published += () =>
        {
            if (tariff.Windows is { } windows && tariff.Origin is { } origin)
            {
                Save(windows, origin, currency);
            }
        };
    }

    private (IReadOnlyList<TariffWindow> Windows, TariffOrigin Origin)? Load(string currency, DateTimeOffset now)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SavedTariff>(File.ReadAllText(Path), Json);
            if (saved?.Windows is not { Count: > 0 } savedWindows || saved.Origin is not { } origin)
            {
                logger.LogWarning("{Path} holds no windows, so no external tariff is restored.", Path);
                return null;
            }

            var windows = savedWindows
                .Select(window => new TariffWindow(new TimeWindow(window.Start, window.End), window.Rate, window.PricePerKwh))
                .ToList();

            if (windows[^1].Window.End <= now)
            {
                logger.LogInformation("The external tariff in {Path} ended at {End:yyyy-MM-dd HH:mm}Z; not restored.", Path, windows[^1].Window.End.UtcDateTime);
                return null;
            }

            var (checkedWindows, problems) = ExternalTariffRules.Check(windows, saved.Currency, currency, now);
            if (problems.Count > 0)
            {
                logger.LogWarning("The external tariff in {Path} no longer passes the checks, so it is not restored: {Problems}",
                    Path, string.Join(" ", problems.Select(problem => problem.Message)));
                return null;
            }

            return (checkedWindows, new TariffOrigin(origin.Name, origin.ReceivedAt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogWarning("{Path} could not be read ({Reason}), so no external tariff is restored.", Path, ex.Message);
            return null;
        }
    }

    private void Save(IReadOnlyList<TariffWindow> windows, TariffOrigin origin, string currency)
    {
        var saved = new SavedTariff(
            currency,
            new SavedOrigin(origin.Name, origin.ReceivedAt),
            [.. windows.Select(window => new SavedWindow(window.Window.Start, window.Window.End, window.Rate, window.PricePerKwh))]);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Atomically, as TariffSettingsFile writes: a temporary file beside it, then a rename.
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, Json));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not write {Path} ({Reason}); the external tariff applies, but a restart will not restore it.", Path, ex.Message);
        }
    }

    private sealed record SavedTariff(string Currency, SavedOrigin? Origin, List<SavedWindow>? Windows);

    private sealed record SavedOrigin(string Name, DateTimeOffset ReceivedAt);

    private sealed record SavedWindow(DateTimeOffset Start, DateTimeOffset End, TariffRate Rate, decimal? PricePerKwh);
}
