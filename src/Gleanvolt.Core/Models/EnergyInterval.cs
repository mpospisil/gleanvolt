namespace Gleanvolt.Core.Models;

/// <summary>
/// One fixed-length slice of the site's energy history — what the roof made, what the forecast said
/// it would make, what crossed the meter, what the car took, and where the home battery sat while it
/// happened.
///
/// <para><b>This is not a sample.</b> A <see cref="ChargingSessionSample"/> is an instant, recorded
/// only while a car is plugged in, and its totals run from the start of that session. An interval is
/// a <em>bucket</em>: it exists for every quarter hour of every day, charging or not, and its figures
/// belong to that window alone. Summing a day's buckets gives the day; no arithmetic on session
/// samples can, because the sun goes on shining between sessions.</para>
///
/// <para><b>kWh, not Wh.</b> Everything else in this codebase integrates in watt-hours, and this
/// record deliberately does not: it is the analytics surface, read by spreadsheets and notebooks
/// rather than by control code, and the unit those are written in is the kilowatt-hour.</para>
///
/// <para><b>Nothing here is netted.</b> Import and export are separate columns, and so are battery
/// charge and discharge, because a quarter hour that exported 0.4 kWh and imported 0.4 kWh is not the
/// same event as one that did neither — and a net of zero cannot tell them apart afterwards.</para>
/// </summary>
/// <param name="PeriodStart">Start of the window, inclusive. Aligned to the interval from the UTC epoch.</param>
/// <param name="PeriodEnd">End of the window, exclusive. Always <c>PeriodStart + Interval</c>.</param>
/// <param name="TimeZoneId">
/// The IANA zone the site was running in. Stored rather than an offset for the same reason sessions
/// store it: an offset frozen at write time is wrong for half the year.
/// </param>
/// <param name="LocalDate">
/// The local calendar day <see cref="PeriodStart"/> fell on, so "group by day" is a column rather than
/// a timezone conversion inside every query. Derived, never independent — it is what the zone above
/// says about the instant above.
/// </param>
/// <param name="SolarKwh">PV produced on site over the window. Never negative.</param>
/// <param name="ForecastSolarKwh">
/// What the forecast expected the roof to make over the same window. <b>Null when no forecast covered
/// any part of it</b> — an honest gap, rather than a zero that reads as "predicted nothing". When only
/// part of the window was covered the figure is that part alone, so a bucket straddling a forecast
/// refresh under-reports rather than inventing.
/// </param>
/// <param name="GridImportKwh">Energy drawn from the grid. Export is not subtracted from it.</param>
/// <param name="GridExportKwh">Energy pushed to the grid, as a positive number.</param>
/// <param name="EvKwh">Energy delivered to the car by the charger, measured at the charger.</param>
/// <param name="BatteryChargeKwh">Energy into the home battery, as measured at the pack.</param>
/// <param name="BatteryDischargeKwh">Energy out of the home battery, as a positive number.</param>
/// <param name="SocStartPercent">Home battery SOC held at <see cref="PeriodStart"/>.</param>
/// <param name="SocEndPercent">SOC at the last reading inside the window.</param>
/// <param name="SocMinPercent">Lowest SOC observed in the window.</param>
/// <param name="SocMaxPercent">Highest SOC observed in the window.</param>
/// <param name="SocMeanPercent">
/// SOC averaged over <em>time</em>, not over samples: a reading that stood for ten minutes counts ten
/// times one that stood for one. Sample-count averaging would let a burst of polls during a Modbus
/// retry storm drag the figure toward whatever the pack happened to be doing that second.
/// </param>
/// <param name="Covered">
/// How much of the window was actually observed. Below the full interval the service was starting,
/// stopping, or had lost the inverter — and every energy figure above is short by the same fraction.
/// <b>Analytics must read this before trusting a row</b>, or a restart at 09:07 becomes "the sun went
/// out for seven minutes".
/// </param>
/// <param name="SampleCount">How many poll snapshots fed the window. Diagnostic only.</param>
public sealed record EnergyInterval(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string TimeZoneId,
    DateOnly LocalDate,
    double SolarKwh,
    double? ForecastSolarKwh,
    double GridImportKwh,
    double GridExportKwh,
    double EvKwh,
    double BatteryChargeKwh,
    double BatteryDischargeKwh,
    double SocStartPercent,
    double SocEndPercent,
    double SocMinPercent,
    double SocMaxPercent,
    double SocMeanPercent,
    TimeSpan Covered,
    int SampleCount)
{
    /// <summary>The window's nominal length, whatever fraction of it was observed.</summary>
    public TimeSpan Duration => PeriodEnd - PeriodStart;

    /// <summary>
    /// The observed fraction of the window, 0 to 1. A row below ~0.99 is partial; see <see cref="Covered"/>.
    /// </summary>
    public double Coverage => Duration > TimeSpan.Zero ? Covered / Duration : 0;

    /// <summary>
    /// Everything the house consumed over the window, the car included — the residual of the energy
    /// balance, and the reason the battery's two columns are here at all:
    /// <code>House = Solar + Import - Export - Battery charge + Battery discharge</code>
    /// Not stored, because it is exactly this and storing it would create a second version that could
    /// disagree with the columns it came from.
    /// </summary>
    public double HouseLoadKwh =>
        SolarKwh + GridImportKwh - GridExportKwh - BatteryChargeKwh + BatteryDischargeKwh;

    /// <summary>House consumption with the car taken out — the "Other Loads" residual, per window.</summary>
    public double OtherLoadsKwh => HouseLoadKwh - EvKwh;
}
