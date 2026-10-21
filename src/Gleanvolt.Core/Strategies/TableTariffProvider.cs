using System.Text.Json;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The tariff strategy the owner fills in (issue #265): low-tariff periods for weekdays and for the
/// weekend, typed on Settings, kept under <c>"table"</c> in <c>tariff.json</c> and expanded by
/// <see cref="TariffExpansion"/> for today and tomorrow.
/// </summary>
public sealed class TableTariffProvider : ITariffProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Id => TariffProviderIds.Table;

    public string Name => "Table";

    public IReadOnlyList<TariffProblem> Check(JsonElement settings) =>
        Read(settings) is { } table
            ? TariffRules.CheckTable(table)
            : [new TariffProblem(TariffFields.Table, null, "The table could not be read.")];

    public JsonElement Normalize(JsonElement settings) =>
        Read(settings) is { } table ? Write(TariffRules.Normalize(table)) : settings;

    public TariffTimeline? Expand(TariffContext context) =>
        Read(context.Settings) is { } table ? TariffExpansion.Expand(table, context.Zone, context.Today, context.Prices) : null;

    /// <summary>
    /// The table in <paramref name="settings"/>: empty when none is saved yet, null when what is there
    /// is not a table at all.
    /// </summary>
    public static TariffTable? Read(JsonElement settings)
    {
        if (settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return TariffTable.Empty;
        }

        if (settings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var stored = settings.Deserialize<Stored>(Json);
            return new TariffTable(Periods(stored?.Weekday), Periods(stored?.Weekend));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><paramref name="table"/> as its section of <c>tariff.json</c>.</summary>
    public static JsonElement Write(TariffTable table) =>
        JsonSerializer.SerializeToElement(new Stored([.. table.Weekday], [.. table.Weekend]), Json);

    // A row with a missing time is kept as an empty string, so Check reports it as "Not a time" on its
    // row rather than the whole section as unreadable.
    private static List<TariffPeriod> Periods(List<TariffPeriod?>? periods) =>
        [.. (periods ?? []).Select(period => new TariffPeriod(period?.Start ?? string.Empty, period?.End ?? string.Empty))];

    private sealed record Stored(List<TariffPeriod?>? Weekday, List<TariffPeriod?>? Weekend);
}
