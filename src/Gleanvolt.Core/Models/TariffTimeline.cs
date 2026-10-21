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

/// <summary>One stretch of a single rate, half-open like every <see cref="TimeWindow"/>.</summary>
public sealed record TariffWindow(TimeWindow Window, TariffRate Rate);

/// <summary>
/// The tariff as concrete instants: alternating low and high stretches that tile the timeline without
/// a gap, from the start of today to at least the end of tomorrow, local time.
///
/// <para><b>Two neighbours never share a rate.</b> A night tariff is entered as 22:00–24:00 and
/// 00:00–06:00, one period per day, and the expansion joins them into one window. So
/// <see cref="NextChangeAfter"/> at 23:00 answers 06:00, which is when something actually changes,
/// rather than a midnight that changes nothing.</para>
/// </summary>
/// <param name="Windows">In time order, each starting where the one before ends.</param>
/// <param name="Prices">What each rate costs, or null when the owner has not entered prices.</param>
public sealed record TariffTimeline(IReadOnlyList<TariffWindow> Windows, TariffPrices? Prices)
{
    /// <summary>The window covering <paramref name="instant"/>, or null outside the timeline.</summary>
    public TariffWindow? WindowAt(DateTimeOffset instant) =>
        Windows.FirstOrDefault(window => window.Window.Covers(instant));

    /// <summary>The rate at <paramref name="instant"/>, or null outside the timeline.</summary>
    public TariffRate? RateAt(DateTimeOffset instant) => WindowAt(instant)?.Rate;

    /// <summary>What a grid kWh costs at <paramref name="instant"/>, or null without prices or outside the timeline.</summary>
    public decimal? PriceAt(DateTimeOffset instant) =>
        Prices is { } prices && RateAt(instant) is { } rate ? prices.For(rate) : null;

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
