namespace Gleanvolt.Core.Strategies;

/// <summary>
/// What the home battery has lent the car today, and how much of it is still <b>outstanding</b> —
/// lending less everything the pack has charged back since, floored at zero.
///
/// <para>Stateful but framework-free, in the same spirit as <see cref="EnergyIntegrator"/>, which it
/// uses for the cumulative half. The reason for the second figure (issue #223): the daily cap used to
/// count every watt-hour ever lent in the day, so a pack that lent 2 kWh in the morning, was refilled
/// to 100% by one o'clock and then met a 3 kW afternoon was refused, though it was physically in the
/// state it started the day in. A cap on lending is a cap on wear, and wear is what the pack is
/// <em>still</em> down, not what it was down at breakfast.</para>
///
/// <para>Recovery is clamped every cycle rather than netted over the day, which is what stops it
/// running into credit: charging that happened <em>before</em> anything was lent is the pack doing its
/// own job, not a prepayment against a loan nobody has asked for.</para>
/// </summary>
public sealed class BatteryLoanLedger
{
    private readonly EnergyIntegrator _lent;
    private readonly TimeSpan _maxGap;

    private DateTimeOffset? _lastSample;
    private double _loanWatts;
    private double _batteryWatts;

    /// <param name="maxGap">
    /// How long a silence may be integrated across before it is treated as lost time. Defaults to five
    /// minutes, matching <see cref="EnergyIntegrator"/>: a service that was asleep neither lent nor
    /// recovered anything we can account for.
    /// </param>
    public BatteryLoanLedger(TimeSpan? maxGap = null)
    {
        _maxGap = maxGap ?? TimeSpan.FromMinutes(5);
        _lent = new EnergyIntegrator(_maxGap);
    }

    /// <summary>Everything lent since the last <see cref="Reset"/>, in watt-hours. What the owner is shown.</summary>
    public double LentWattHours => _lent.EnergyWattHours;

    /// <summary>The part of it the pack has not charged back yet, in watt-hours. What the cap is measured against.</summary>
    public double OutstandingWattHours { get; private set; }

    /// <summary>
    /// Records one cycle and returns the outstanding total.
    /// </summary>
    /// <param name="timestamp">The reading's instant.</param>
    /// <param name="loanPowerWatts">
    /// The loan <b>commanded</b> this cycle, not the pack's measured discharge — the loan is our own
    /// decision, while the battery's actual power also carries house load and PV swings.
    /// </param>
    /// <param name="batteryPowerWatts">
    /// The pack's measured power, positive charging. Only the charging half is recovery; discharging is
    /// what the loan already accounts for.
    /// </param>
    public double Add(DateTimeOffset timestamp, double loanPowerWatts, double batteryPowerWatts)
    {
        _lent.Add(timestamp, loanPowerWatts);

        if (_lastSample is { } previous)
        {
            var elapsed = timestamp - previous;
            if (elapsed > TimeSpan.Zero && elapsed <= _maxGap)
            {
                var hours = elapsed.TotalHours;
                var lentWh = Math.Max(0, _loanWatts) * hours;
                var recoveredWh = Math.Max(0, _batteryWatts) * hours;

                OutstandingWattHours = Math.Max(0, OutstandingWattHours + lentWh - recoveredWh);
            }
        }

        _lastSample = timestamp;
        _loanWatts = loanPowerWatts;
        _batteryWatts = batteryPowerWatts;

        return OutstandingWattHours;
    }

    /// <summary>Clears both totals and forgets the last reading — a new day.</summary>
    public void Reset()
    {
        _lent.Reset();
        OutstandingWattHours = 0;
        _lastSample = null;
        _loanWatts = 0;
        _batteryWatts = 0;
    }
}
