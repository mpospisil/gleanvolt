namespace Gleanvolt.Core.Models;

/// <summary>Where the tariff schedule comes from (issue #265).</summary>
public enum TariffProvider
{
    /// <summary>No tariff: every consumer treats the grid as costing the same at every hour.</summary>
    None,

    /// <summary>The low-tariff periods the owner typed in on Settings.</summary>
    Table,
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
/// The owner's tariff as stored in <c>data/tariff.json</c> and edited on Settings: only the
/// low-tariff periods are listed, and every other minute is high tariff.
///
/// <para><b>A period belongs to one day.</b> 22:00–06:00 is two rows, 22:00–24:00 and 00:00–06:00;
/// <see cref="TariffExpansion"/> joins them back together across midnight. One rule — start before
/// end — is easier to check, explain and draw than a period that may or may not wrap.</para>
/// </summary>
/// <param name="Provider">Whether this table is in use at all.</param>
/// <param name="Weekday">Monday to Friday's low-tariff periods.</param>
/// <param name="Weekend">Saturday and Sunday's.</param>
/// <param name="LowPricePerKwh">The low-tariff price, or null when not entered.</param>
/// <param name="HighPricePerKwh">The high-tariff price, or null when not entered.</param>
public sealed record TariffTable(
    TariffProvider Provider,
    IReadOnlyList<TariffPeriod> Weekday,
    IReadOnlyList<TariffPeriod> Weekend,
    decimal? LowPricePerKwh = null,
    decimal? HighPricePerKwh = null)
{
    /// <summary>No tariff: what a missing or unreadable file means.</summary>
    public static TariffTable None { get; } = new(TariffProvider.None, [], []);

    /// <summary>The list for <paramref name="days"/>.</summary>
    public IReadOnlyList<TariffPeriod> PeriodsFor(TariffDays days) => days == TariffDays.Weekday ? Weekday : Weekend;

    /// <summary>Which list applies on <paramref name="date"/>.</summary>
    public static TariffDays DaysOf(DateOnly date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? TariffDays.Weekend : TariffDays.Weekday;

    /// <summary>Both prices in <paramref name="currency"/>, or null unless both were entered.</summary>
    public TariffPrices? PricesIn(string currency) =>
        LowPricePerKwh is { } low && HighPricePerKwh is { } high ? new TariffPrices(low, high, currency) : null;
}

/// <summary>What a problem in a <see cref="TariffTable"/> is about, so the form can show it in place.</summary>
public enum TariffField
{
    /// <summary>A row of the weekday list; <see cref="TariffProblem.Row"/> says which.</summary>
    Weekday,

    /// <summary>A row of the weekend list.</summary>
    Weekend,

    /// <summary>The low-tariff price.</summary>
    LowPrice,

    /// <summary>The high-tariff price.</summary>
    HighPrice,

    /// <summary>The table as a whole.</summary>
    Table,
}

/// <summary>
/// One thing wrong with a table. An error stops a save; a warning is shown and saved through, because
/// it describes something odd rather than something unusable.
/// </summary>
/// <param name="Field">What it is about.</param>
/// <param name="Row">The row, as an index into the list as given, for <see cref="TariffField.Weekday"/> and <see cref="TariffField.Weekend"/>.</param>
/// <param name="Message">What to show beside it.</param>
/// <param name="IsWarning">True when saving is still allowed.</param>
public sealed record TariffProblem(TariffField Field, int? Row, string Message, bool IsWarning = false);

/// <summary>The outcome of a tariff save: saved, or the problems that stopped it, with any warnings either way.</summary>
public sealed record TariffSaveResult(bool Saved, IReadOnlyList<TariffProblem> Problems)
{
    /// <summary>The problems that stopped the save.</summary>
    public IEnumerable<TariffProblem> Errors => Problems.Where(problem => !problem.IsWarning);

    /// <summary>What was saved through.</summary>
    public IEnumerable<TariffProblem> Warnings => Problems.Where(problem => problem.IsWarning);
}
