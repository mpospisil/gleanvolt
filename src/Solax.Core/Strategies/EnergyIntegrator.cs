namespace Solax.Core.Strategies;

/// <summary>
/// Accumulates watt-hours from a series of power samples, using the interval between consecutive
/// samples as each sample's duration (trapezoid-free: the previous power is held until the next
/// reading, which matches how the poll loop actually observes the hardware).
///
/// <para>Stateful but framework-free, in the same spirit as <see cref="SurplusMovingAverage"/>. Used
/// for session energy delivered to the car and for energy loaned out of the home battery.</para>
/// </summary>
public sealed class EnergyIntegrator
{
    // A gap longer than this means the service was asleep, the poll loop stalled, or a reading was
    // lost -- integrating across it would invent energy that was never measured, so the gap is
    // skipped and integration resumes from the new sample.
    private readonly TimeSpan _maxGap;

    private DateTimeOffset? _lastSample;
    private double _lastPowerWatts;

    public EnergyIntegrator(TimeSpan? maxGap = null) => _maxGap = maxGap ?? TimeSpan.FromMinutes(5);

    /// <summary>Energy accumulated since the last <see cref="Reset"/>, in watt-hours.</summary>
    public double EnergyWattHours { get; private set; }

    /// <summary>Adds a power sample and returns the running total in watt-hours.</summary>
    public double Add(DateTimeOffset timestamp, double powerWatts)
    {
        if (_lastSample is { } previous)
        {
            var elapsed = timestamp - previous;
            if (elapsed > TimeSpan.Zero && elapsed <= _maxGap)
            {
                EnergyWattHours += _lastPowerWatts * elapsed.TotalHours;
            }
        }

        _lastSample = timestamp;
        _lastPowerWatts = powerWatts;
        return EnergyWattHours;
    }

    /// <summary>Clears the total and forgets the last sample (e.g. a new session, or a new day).</summary>
    public void Reset()
    {
        EnergyWattHours = 0;
        _lastSample = null;
        _lastPowerWatts = 0;
    }
}
