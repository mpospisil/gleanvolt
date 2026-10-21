namespace Gleanvolt.Core.Models;

/// <summary>Which of a two-rate tariff's prices the grid costs at a given moment (issue #265).</summary>
public enum TariffRate
{
    /// <summary>The cheap rate: Czech HDO's <i>NT</i>.</summary>
    Low,

    /// <summary>The expensive rate, <i>VT</i>: every moment the table does not list as low.</summary>
    High,
}

/// <summary>What a kWh from the grid costs at each rate, in <paramref name="Currency"/>.</summary>
/// <param name="LowPerKwh">The low-tariff price per kWh.</param>
/// <param name="HighPerKwh">The high-tariff price per kWh.</param>
/// <param name="Currency">An ISO 4217 code, <c>Tariff:Currency</c>: what both prices are in.</param>
public sealed record TariffPrices(decimal LowPerKwh, decimal HighPerKwh, string Currency)
{
    /// <summary>The price of <paramref name="rate"/>.</summary>
    public decimal For(TariffRate rate) => rate == TariffRate.Low ? LowPerKwh : HighPerKwh;
}

/// <summary>
/// The tariff as a mode set to <b>low tariff only</b> sees it (issue #274): the rate now and when it
/// next changes. Handed to a controller only when the option is on for the mode driving <i>and</i> a
/// tariff is known; null otherwise, which every controller reads as no restriction.
/// </summary>
/// <param name="Rate">The rate now.</param>
/// <param name="NextChange">When it next changes, or null when that is beyond what the tariff knows.</param>
public sealed record TariffGate(TariffRate Rate, DateTimeOffset? NextChange)
{
    /// <summary>Whether a low-tariff-only mode has to wait now.</summary>
    public bool IsHigh => Rate == TariffRate.High;

    /// <summary>The gate for <paramref name="instant"/> on <paramref name="timeline"/>, or null when the timeline does not cover it.</summary>
    public static TariffGate? At(TariffTimeline? timeline, DateTimeOffset instant) =>
        timeline?.WindowAt(instant) is { } window ? new TariffGate(window.Rate, timeline.NextChangeAfter(instant)) : null;
}

/// <summary>One stretch of a single rate, half-open like every <see cref="TimeWindow"/>.</summary>
/// <param name="Window">When.</param>
/// <param name="Rate">Which rate: what Fast and SolarGrid act on.</param>
/// <param name="PricePerKwh">
/// This window's own price, for a strategy that knows one (a spot price per slot), or null for the
/// shared price of <paramref name="Rate"/>. It is how a price that changes every quarter hour fits a
/// two-rate model: the strategy decides which slots are <see cref="TariffRate.Low"/>, and the real
/// price travels with each one.
/// </param>
public sealed record TariffWindow(TimeWindow Window, TariffRate Rate, decimal? PricePerKwh = null);

/// <summary>
/// Everything a strategy may need to expand its settings, as one record, so a new input later is a new
/// property rather than a changed signature on every <see cref="Interfaces.ITariffProvider"/>.
/// </summary>
/// <param name="Now">When the expansion is made.</param>
/// <param name="Today">The local date the timeline starts on.</param>
/// <param name="Zone">The zone local times are in.</param>
/// <param name="Prices">The shared prices, or null when the owner has not entered both.</param>
/// <param name="Settings">The strategy's own section of <c>tariff.json</c>; undefined when none is saved.</param>
public sealed record TariffContext(
    DateTimeOffset Now,
    DateOnly Today,
    TimeZoneInfo Zone,
    TariffPrices? Prices,
    System.Text.Json.JsonElement Settings);

/// <summary>
/// The tariff as concrete instants: alternating low and high stretches that tile the timeline without
/// a gap, as far ahead as the strategy knows — for the Table strategy, from the start of today to the
/// end of tomorrow, local time.
///
/// <para><b>Two neighbours never share a rate.</b> A night tariff is entered as 22:00–24:00 and
/// 00:00–06:00, one period per day, and the expansion joins them into one window. So
/// <see cref="NextChangeAfter"/> at 23:00 answers 06:00, which is when something actually changes,
/// rather than a midnight that changes nothing.</para>
/// </summary>
/// <param name="Windows">In time order, each starting where the one before ends.</param>
/// <param name="Prices">What each rate costs, or null when the owner has not entered prices.</param>
/// <param name="ProviderId">The strategy that produced it, for a surface that names it.</param>
public sealed record TariffTimeline(IReadOnlyList<TariffWindow> Windows, TariffPrices? Prices, string ProviderId = "")
{
    /// <summary>The window covering <paramref name="instant"/>, or null outside the timeline.</summary>
    public TariffWindow? WindowAt(DateTimeOffset instant) =>
        Windows.FirstOrDefault(window => window.Window.Covers(instant));

    /// <summary>The rate at <paramref name="instant"/>, or null outside the timeline.</summary>
    public TariffRate? RateAt(DateTimeOffset instant) => WindowAt(instant)?.Rate;

    /// <summary>
    /// What a grid kWh costs at <paramref name="instant"/>: the window's own price when it has one, else
    /// the shared price of its rate; null without either, or outside the timeline.
    /// </summary>
    public decimal? PriceAt(DateTimeOffset instant) =>
        WindowAt(instant) is not { } window ? null : window.PricePerKwh ?? Prices?.For(window.Rate);

    /// <summary>
    /// When the rate next changes after <paramref name="instant"/>, or null when it does not change
    /// again before the timeline ends: the timeline's last edge is its horizon, not a change.
    /// </summary>
    public DateTimeOffset? NextChangeAfter(DateTimeOffset instant)
    {
        for (var i = 0; i < Windows.Count - 1; i++)
        {
            if (Windows[i].Window.End > instant)
            {
                return Windows[i].Window.End;
            }
        }

        return null;
    }

    /// <summary>The low-tariff windows alone, which is what a chart shades.</summary>
    public IEnumerable<TimeWindow> LowWindows =>
        Windows.Where(window => window.Rate == TariffRate.Low).Select(window => window.Window);
}
