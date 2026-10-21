using System.Text.Json;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The ids of the tariff strategies this build has (issue #265). Strings rather than an enum, so a new
/// strategy is a new <see cref="Interfaces.ITariffProvider"/> and nothing shared has to change: these
/// two are named only because the host registers them and the UI treats <see cref="None"/> specially.
/// </summary>
public static class TariffProviderIds
{
    /// <summary>No tariff: every consumer treats the grid as costing the same at every hour.</summary>
    public const string None = "None";

    /// <summary>The low-tariff periods the owner typed in on Settings.</summary>
    public const string Table = "Table";
}

/// <summary>Which of the table's two lists a period is in.</summary>
public enum TariffDays
{
    /// <summary>Monday to Friday, and (until there is a holiday calendar) public holidays too.</summary>
    Weekday,

    /// <summary>Saturday and Sunday.</summary>
    Weekend,
}

/// <summary>
/// One low-tariff period within one day, as typed: <c>HH:mm</c> strings rather than parsed times,
/// because the form has to hold <i>"16:6"</i> long enough to say it is not a time, and the end may be
/// <c>24:00</c>, which no <see cref="TimeOnly"/> can be.
/// </summary>
public sealed record TariffPeriod(string Start, string End);

/// <summary>
/// The <see cref="TariffProviderIds.Table"/> strategy's settings: only the low-tariff periods are
/// listed, and every other minute is high tariff.
///
/// <para><b>A period belongs to one day.</b> 22:00–06:00 is two rows, 22:00–24:00 and 00:00–06:00;
/// <see cref="TariffExpansion"/> joins them back together across midnight. One rule — start before
/// end — is easier to check, explain and draw than a period that may or may not wrap.</para>
/// </summary>
/// <param name="Weekday">Monday to Friday's low-tariff periods.</param>
/// <param name="Weekend">Saturday and Sunday's.</param>
public sealed record TariffTable(IReadOnlyList<TariffPeriod> Weekday, IReadOnlyList<TariffPeriod> Weekend)
{
    /// <summary>No periods at all: high tariff around the clock.</summary>
    public static TariffTable Empty { get; } = new([], []);

    /// <summary>The list for <paramref name="days"/>.</summary>
    public IReadOnlyList<TariffPeriod> PeriodsFor(TariffDays days) => days == TariffDays.Weekday ? Weekday : Weekend;

    /// <summary>Which list applies on <paramref name="date"/>.</summary>
    public static TariffDays DaysOf(DateOnly date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? TariffDays.Weekend : TariffDays.Weekday;
}

/// <summary>
/// Everything <c>data/tariff.json</c> holds: which strategy is selected, the prices every strategy
/// shares, and each strategy's own settings under its own key.
///
/// <para><b>Sections are opaque here.</b> Only the strategy a section belongs to reads it. The rest of
/// the system carries it as JSON, and keeps it when a different strategy is selected or when this build
/// does not even know the strategy, so switching away and back, or running an older build for a while,
/// never loses anybody's settings.</para>
///
/// <para><b>Prices are shared.</b> A distributor's feed knows when the rate changes, not what the owner
/// pays, so the prices sit beside the selection rather than inside a section.</para>
/// </summary>
/// <param name="Provider">The selected strategy's id, as typed in the file; matched case-insensitively.</param>
/// <param name="LowPricePerKwh">The low-tariff price, or null when not entered.</param>
/// <param name="HighPricePerKwh">The high-tariff price, or null when not entered.</param>
/// <param name="Sections">Each strategy's settings, keyed by <see cref="SectionKey"/>.</param>
public sealed record TariffSettings(
    string Provider,
    decimal? LowPricePerKwh,
    decimal? HighPricePerKwh,
    IReadOnlyDictionary<string, JsonElement> Sections)
{
    /// <summary>No tariff and nothing stored: what a missing or unreadable file means.</summary>
    public static TariffSettings None { get; } =
        new(TariffProviderIds.None, null, null, new Dictionary<string, JsonElement>());

    /// <summary>Where a strategy's settings live in the file: its id in lower case, <c>"table"</c> for <c>Table</c>.</summary>
    public static string SectionKey(string providerId) => providerId.ToLowerInvariant();

    /// <summary>Whether <see cref="Provider"/> is <paramref name="providerId"/>.</summary>
    public bool Selects(string providerId) => string.Equals(Provider, providerId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The settings of <paramref name="providerId"/>, or an undefined element when it has none yet,
    /// which every strategy reads as its own defaults.
    /// </summary>
    public JsonElement SectionFor(string providerId) =>
        Sections.TryGetValue(SectionKey(providerId), out var section) ? section : default;

    /// <summary>
    /// These settings with <paramref name="providerId"/>'s section replaced, or removed when
    /// <paramref name="section"/> is undefined (a strategy with nothing to store); every other section is kept.
    /// </summary>
    public TariffSettings WithSection(string providerId, JsonElement section)
    {
        var sections = new Dictionary<string, JsonElement>(Sections, StringComparer.OrdinalIgnoreCase);

        if (section.ValueKind == JsonValueKind.Undefined)
        {
            sections.Remove(SectionKey(providerId));
        }
        else
        {
            sections[SectionKey(providerId)] = section.Clone();
        }

        return this with { Sections = sections };
    }

    /// <summary>Both prices in <paramref name="currency"/>, or null unless both were entered.</summary>
    public TariffPrices? PricesIn(string currency) =>
        LowPricePerKwh is { } low && HighPricePerKwh is { } high ? new TariffPrices(low, high, currency) : null;
}

/// <summary>What a <see cref="TariffProblem"/> is about, so a form can show it in place and the API can name it.</summary>
public static class TariffFields
{
    /// <summary>The selection itself.</summary>
    public const string Provider = "provider";

    /// <summary>The low-tariff price.</summary>
    public const string LowPrice = "lowPricePerKwh";

    /// <summary>The high-tariff price.</summary>
    public const string HighPrice = "highPricePerKwh";

    /// <summary>The Table strategy's settings as a whole.</summary>
    public const string Table = "table";

    /// <summary>A row of the Table strategy's weekday list; <see cref="TariffProblem.Row"/> says which.</summary>
    public const string TableWeekday = "table.weekday";

    /// <summary>A row of its weekend list.</summary>
    public const string TableWeekend = "table.weekend";
}

/// <summary>
/// One thing wrong with tariff settings. An error stops a save; a warning is shown and saved through,
/// because it describes something odd rather than something unusable.
/// </summary>
/// <param name="Field">What it is about: one of <see cref="TariffFields"/>, or a strategy's own path under its section key.</param>
/// <param name="Row">The row, as an index into the list as given, for a field that is a list.</param>
/// <param name="Message">What to show beside it.</param>
/// <param name="IsWarning">True when saving is still allowed.</param>
public sealed record TariffProblem(string Field, int? Row, string Message, bool IsWarning = false);

/// <summary>The outcome of a tariff save: saved, or the problems that stopped it, with any warnings either way.</summary>
public sealed record TariffSaveResult(bool Saved, IReadOnlyList<TariffProblem> Problems)
{
    /// <summary>The problems that stopped the save.</summary>
    public IEnumerable<TariffProblem> Errors => Problems.Where(problem => !problem.IsWarning);

    /// <summary>What was saved through.</summary>
    public IEnumerable<TariffProblem> Warnings => Problems.Where(problem => problem.IsWarning);
}
