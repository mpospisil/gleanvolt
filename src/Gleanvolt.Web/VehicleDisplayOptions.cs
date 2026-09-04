namespace Gleanvolt.Web;

/// <summary>
/// The pieces of vehicle configuration the UI needs: how old a reading may be before it is shown as
/// stale, and what the car's pack holds — which is what decides whether a target can be asked for in
/// state of charge at all.
///
/// <para>Same arrangement as <see cref="WebBuildInfo"/>, and for the same reason — the
/// <c>"Vehicle"</c> section is bound in <c>Gleanvolt.Hosting</c>, an assembly this one must not
/// reference, so the host hands the UI the single value it has to render rather than the whole options
/// object.</para>
/// </summary>
/// <param name="MaxAge">How old a reading may be before it is shown as stale.</param>
/// <param name="BatteryCapacityKWh">
/// The car's usable capacity, or 0 when it has not been configured. Zero withdraws the SOC-based
/// target: see <see cref="Core.Strategies.VehicleTargetEnergy"/>.
/// </param>
/// <param name="ChargeEfficiency">Charger-meter → cells efficiency, applied to a SOC-based target.</param>
public sealed record VehicleDisplayOptions(
    TimeSpan MaxAge,
    double BatteryCapacityKWh = 0,
    double ChargeEfficiency = 0.9,
    bool OnDemandOnly = false)
{
    /// <summary>Whether a target may be asked for as a state of charge — i.e. whether a capacity is known.</summary>
    public bool CanTargetSoc => BatteryCapacityKWh > 0;

    /// <summary>
    /// Whether a reading may be shown without somebody having just asked for it (issue #168).
    ///
    /// <para>False in on-demand mode, where the point is that a figure nobody requested is a figure
    /// nobody checked the age of. The reading still exists and the API still serves it; the dashboard
    /// simply does not put a number in front of an eye that will read it as current.</para>
    /// </summary>
    public bool ShowsAmbientReading => !OnDemandOnly;
}
