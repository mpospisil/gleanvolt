namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the targeted charge mode (issue #80). Bound from the
/// <c>"ChargeControl:Targeted"</c> section — nested inside <see cref="ChargeControlOptions"/>'s
/// section, because it refines that feature rather than being a separate one.
///
/// <para>Everything the <em>owner</em> supplies for this mode — the energy and the departure time —
/// is a runtime request rather than configuration, and is deliberately not settable here: it belongs
/// to one trip, not to the installation.</para>
/// </summary>
public sealed class TargetedChargeOptions
{
    public const string SectionName = "ChargeControl:Targeted";

    /// <summary>
    /// How far before the stated departure the plan finishes. "Ready at 07:00" must not mean "still
    /// charging at 07:00": the owner needs to be able to unplug and go, and a car that is still
    /// drawing when they get to it is not ready.
    /// </summary>
    public TimeSpan SafetyMargin { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far ahead a departure may be set. Solcast's cached forecast runs days ahead, but a target
    /// four days out is a promise the forecast cannot keep — and the request does not survive a
    /// restart, so a multi-day one is a promise this service cannot keep either.
    /// </summary>
    public TimeSpan MaxHorizon { get; init; } = TimeSpan.FromHours(36);
}
