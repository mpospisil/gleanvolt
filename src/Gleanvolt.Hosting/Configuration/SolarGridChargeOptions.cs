using Gleanvolt.Core.Enums;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the solar-grid charge mode. Bound from the <c>"ChargeControl:SolarGrid"</c>
/// section, nested inside <see cref="ChargeControlOptions"/>'s for the same reason the forecast and
/// targeted sections are: it refines charge control rather than being a separate feature.
///
/// <para>Deliberately short. The dwell timers are the forecast section's (<c>MinRunTime</c>,
/// <c>MinPauseTime</c>), the smoothing window and hysteresis are charge control's, and the forecast's
/// staleness limit is the forecast section's — they describe the hardware and the provider, not this
/// strategy, and a second copy of any of them would only drift.</para>
/// </summary>
public sealed class SolarGridChargeOptions
{
    public const string SectionName = "ChargeControl:SolarGrid";

    /// <summary>
    /// The boot default for the minimum smoothed surplus, in watts, below which the car pauses instead
    /// of being bridged from the grid. Settable at runtime from the web UI and Home Assistant; a runtime
    /// change does not survive a restart.
    ///
    /// <para>On three phases the charger's floor is ~4.1 kW, so the default 2 kW means "bridge at most
    /// ~2.1 kW from the grid". Set it at or above the floor and the grid never helps at all: the mode
    /// is then plain surplus charging with no battery gate, ending at sunset.</para>
    /// </summary>
    public double MinSurplusWatts { get; init; } = 2000;

    /// <summary>
    /// Which forecast band decides whether sun is still to come. The median by default, unlike the day
    /// plan's P10: that one underwrites a guarantee, while this one only decides whether an idle car
    /// gives up for the day — and giving up on a sunny afternoon is the expensive mistake here.
    /// </summary>
    public ForecastConfidence ForecastConfidence { get; init; } = ForecastConfidence.P50;
}
