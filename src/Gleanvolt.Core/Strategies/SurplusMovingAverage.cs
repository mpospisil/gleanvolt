namespace Gleanvolt.Core.Strategies;

/// <summary>
/// A time-windowed rolling average of the solar surplus.
///
/// Raw PV generation is erratic — a single 15-second cloud can swing it by kilowatts. Driving the
/// charger straight off the instantaneous value would interrupt a multi-hour charging session for a
/// momentary shadow. Averaging over a window (3 minutes by default) smooths that out, so the
/// controller reacts to real trends rather than noise.
///
/// Samples are averaged unweighted; with a regular poll interval that is equivalent to a time
/// average. Samples older than the window are evicted on each <see cref="Add"/>.
/// </summary>
public sealed class SurplusMovingAverage
{
    private readonly TimeSpan _window;
    private readonly Queue<(DateTimeOffset Timestamp, double Watts)> _samples = new();
    private double _sum;

    public SurplusMovingAverage(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "Averaging window must be positive.");
        }

        _window = window;
    }

    /// <summary>Number of samples currently inside the window.</summary>
    public int Count => _samples.Count;

    /// <summary>
    /// Adds a surplus sample and returns the average over the window, dropping samples older than it.
    /// </summary>
    public double Add(DateTimeOffset timestamp, double surplusWatts)
    {
        _samples.Enqueue((timestamp, surplusWatts));
        _sum += surplusWatts;

        // Evict anything that has aged out of the window.
        var cutoff = timestamp - _window;
        while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
        {
            _sum -= _samples.Dequeue().Watts;
        }

        return _sum / _samples.Count;
    }

    /// <summary>Discards all samples (e.g. when control is released and history is no longer relevant).</summary>
    public void Reset()
    {
        _samples.Clear();
        _sum = 0;
    }
}
