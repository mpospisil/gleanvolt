using System.Text.Json;
using System.Text.Json.Nodes;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// The tariff settings in <c>data/tariff.json</c> (issue #265): which strategy is selected, the shared
/// prices, and a section per strategy. Checked before anything is written, and held in memory between
/// saves so that <see cref="TariffSchedule"/> never reads the disk on a poll.
///
/// <para><b>Sections it does not understand are kept.</b> Each strategy reads only its own, and a
/// section for a strategy this build does not have — a file written by a newer one — is carried through
/// every save untouched, so running an older build for a while loses nobody's settings.</para>
///
/// <para><b>Never "always low".</b> A missing file is a tariff that was never set up, and an unreadable
/// one is logged as a warning; both are <see cref="TariffSettings.None"/>. A selected section that fails
/// its strategy's check (a hand edit) is read as no selection, with the section kept, so Settings still
/// shows it to be fixed.</para>
/// </summary>
public sealed class TariffSettingsFile : ITariffEditor
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ILogger<TariffSettingsFile> _logger;

    // Two browser tabs, or a tab and the API, saving at once must not interleave a check and a write.
    private readonly Lock _gate = new();

    private TariffSettings _current;

    /// <param name="path">The absolute path of the file, whether or not it exists yet.</param>
    /// <param name="currency">What the prices are in, from <c>Tariff:Currency</c>.</param>
    /// <param name="providers">Every strategy this build has.</param>
    /// <param name="logger">Where loads and saves are recorded.</param>
    public TariffSettingsFile(
        string path,
        string currency,
        IEnumerable<ITariffProvider> providers,
        ILogger<TariffSettingsFile> logger)
    {
        Path = path;
        Currency = currency;
        Providers = [.. providers];
        _logger = logger;
        _current = Load();
    }

    /// <summary>Where the settings are kept.</summary>
    public string Path { get; }

    public string Currency { get; }

    public IReadOnlyList<ITariffProvider> Providers { get; }

    public event Action? Saved;

    public TariffSettings Read()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public IReadOnlyList<TariffProblem> Check(TariffSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Prices mean nothing without a tariff, and stale ones left behind must not stop a switch to None.
        if (settings.Selects(TariffProviderIds.None))
        {
            return [];
        }

        if (Find(settings.Provider) is not { } provider)
        {
            return [new TariffProblem(TariffFields.Provider, null, $"There is no tariff provider called {settings.Provider} in this build.")];
        }

        return [.. TariffRules.CheckPrices(settings.LowPricePerKwh, settings.HighPricePerKwh), .. provider.Check(settings.SectionFor(provider.Id))];
    }

    public TariffSaveResult Save(TariffSettings settings)
    {
        var problems = Check(settings);

        if (problems.Any(problem => !problem.IsWarning))
        {
            return new TariffSaveResult(false, problems);
        }

        var provider = Find(settings.Provider)!;
        var normalized = settings.WithSection(provider.Id, provider.Normalize(settings.SectionFor(provider.Id))) with
        {
            Provider = provider.Id,
        };

        lock (_gate)
        {
            try
            {
                Write(normalized);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TariffSaveResult(
                    false, [.. problems, new TariffProblem(TariffFields.Provider, null, $"Could not write {Path}: {ex.Message}")]);
            }

            _current = normalized;
        }

        _logger.LogInformation("Tariff saved to {Path}: provider {Provider}.", Path, normalized.Provider);

        Saved?.Invoke();
        return new TariffSaveResult(true, problems);
    }

    private ITariffProvider? Find(string id) =>
        Providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));

    private TariffSettings Load()
    {
        if (!File.Exists(Path))
        {
            _logger.LogInformation("No tariff at {Path}; the grid costs the same at every hour until one is saved.", Path);
            return TariffSettings.None;
        }

        TariffSettings settings;

        try
        {
            settings = Parse(File.ReadAllText(Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            // A value of the wrong kind, such as a provider that is a number or a price that is text.
            or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(
                "The tariff at {Path} could not be read ({Reason}), so there is no tariff until it is saved again.",
                Path,
                ex.Message);
            return TariffSettings.None;
        }

        // A strategy this build lacks is kept selected: the schedule says so, and an older build must
        // not quietly rewrite a newer one's choice. One that is here but refuses its own section is not.
        if (Find(settings.Provider) is { } provider
            && provider.Check(settings.SectionFor(provider.Id)).FirstOrDefault(problem => !problem.IsWarning) is { } problem)
        {
            _logger.LogWarning(
                "The {Provider} tariff in {Path} is not usable ({Field}{Row}: {Reason}), so there is no tariff until it is "
                + "fixed on Settings.",
                provider.Name,
                Path,
                problem.Field,
                problem.Row is { } row ? $" row {row + 1}" : string.Empty,
                problem.Message);
            return settings with { Provider = TariffProviderIds.None };
        }

        return settings;
    }

    private static TariffSettings Parse(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject root)
        {
            throw new InvalidDataException("The file is not a JSON object.");
        }

        var provider = TariffProviderIds.None;
        decimal? low = null;
        decimal? high = null;
        var sections = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, node) in root)
        {
            switch (key.ToLowerInvariant())
            {
                case "provider":
                    provider = node?.GetValue<string>() is { Length: > 0 } id ? id : TariffProviderIds.None;
                    break;
                case "lowpriceperkwh":
                    low = node?.GetValue<decimal>();
                    break;
                case "highpriceperkwh":
                    high = node?.GetValue<decimal>();
                    break;
                default:
                    if (node is JsonObject section)
                    {
                        sections[key] = JsonSerializer.SerializeToElement(section);
                    }

                    break;
            }
        }

        return new TariffSettings(provider, low, high, sections);
    }

    // Atomically, as PvSystemOverrides writes: a temporary file beside it, then a rename, so a power
    // cut mid-write leaves the old settings or the new ones and never half of either.
    private void Write(TariffSettings settings)
    {
        var root = new JsonObject
        {
            ["provider"] = settings.Provider,
            ["lowPricePerKwh"] = settings.LowPricePerKwh,
            ["highPricePerKwh"] = settings.HighPricePerKwh,
        };

        foreach (var (key, section) in settings.Sections.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            root[TariffSettings.SectionKey(key)] = JsonNode.Parse(section.GetRawText());
        }

        var directory = System.IO.Path.GetDirectoryName(Path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(Indented));

        try
        {
            File.Move(temporary, Path, overwrite: true);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // A scanner or the indexer holding the target open for a moment; see PvSystemOverrides.Write.
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            File.Move(temporary, Path, overwrite: true);
        }
    }
}
