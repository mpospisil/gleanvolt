namespace Gleanvolt.Core.Models;

/// <summary>
/// The cars this controller charges, bound from the <c>"Ev"</c> configuration section (issue #124).
///
/// <para>The <see cref="PvSystemOptions"/> arrangement, applied to the other side of the cable: the
/// vehicle is described in one place, validated once at startup, and handed to every consumer as a
/// resolved <see cref="EvInfo"/>. A second kind of thing described a second way would be the beginning
/// of not having a pattern at all.</para>
///
/// <para><b>Nothing here is required.</b> An empty section is a valid state and changes nothing: every
/// figure falls back to what the installation already says, which is what makes this safe to land
/// before anybody has filled it in.</para>
/// </summary>
public sealed class EvOptions
{
    public const string SectionName = "Ev";

    /// <summary>
    /// The vehicles, in configuration order. <b>Exactly one is supported</b> — the list exists so the
    /// second one does not need a configuration break, which is the same promise
    /// <see cref="PvSystemOptions.Chargers"/> makes and for the same reason. Nothing selects between
    /// them, and a second entry is refused at startup rather than silently ignored.
    /// </summary>
    public EvVehicleOptions[] Vehicles { get; init; } = [];
}

/// <summary>
/// One car: what it is, what its pack holds, and what it will actually accept from a charger.
///
/// <para>The last of those is the point of the issue. <see cref="Phases"/>,
/// <see cref="MinChargingCurrentAmps"/> and <see cref="MaxChargingCurrentAmps"/> describe <b>the
/// car</b>, and until now the controller had only the charger's equivalents and used them as though
/// they were the same thing.</para>
/// </summary>
public sealed class EvVehicleOptions
{
    /// <summary>
    /// The car's stable identity: a slug, on the same terms as <see cref="PvSystemOptions.Id"/>. It is
    /// what a second vehicle would be selected by, and what a per-car entity would be named after.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What a human calls it ("The ID.4"). Free text; never used as a key.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The manufacturer. Reported, never acted on.</summary>
    public string Make { get; init; } = string.Empty;

    /// <summary>
    /// The model. Reported, never acted on — the same distinction <see cref="PvDeviceOptions.Model"/>
    /// draws: the day a model string selects behaviour is the day a typo changes how the car is
    /// charged.
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// The drive battery's <b>usable</b> capacity in kilowatt-hours — the figure the car's own SOC is a
    /// percentage of, not the gross pack size on the brochure. An ID.4 Pro is 77 usable of 82 gross;
    /// the larger number overshoots every SOC-based target by 6%.
    ///
    /// <para>Zero means "not configured", and costs exactly one thing: the state-of-charge basis on
    /// targeted and fast charges. A guessed pack size would make every such target quietly wrong rather
    /// than visibly unavailable.</para>
    /// </summary>
    public double BatteryCapacityKWh { get; init; }

    /// <summary>
    /// How much of what the charger meters reaches the cells, (0..1]. Applied to SOC-based targets: they
    /// are measured at the charger, and asking for the difference in the pack alone arrives short by the
    /// on-board rectifier's losses.
    /// </summary>
    public double ChargeEfficiency { get; init; } = 0.9;

    /// <summary>
    /// How many phases the car can actually charge on — <b>not</b> how many the wallbox offers.
    ///
    /// <para>This is the field the issue was written around. Every watts↔amps conversion in the
    /// controller runs through a phase count, and until now that count was the charger's. A
    /// single-phase car behind a three-phase wallbox therefore had every power figure in the system
    /// overstated threefold: the deferred fast charge started hours late, and the day plan budgeted
    /// energy the car could never take.</para>
    ///
    /// <para>Null means "not stated", and narrows nothing.</para>
    /// </summary>
    public int? Phases { get; init; }

    /// <summary>
    /// The lowest current the car will actually start on. Null means "not stated".
    ///
    /// <para>Worth setting for a car that refuses low currents: commanded 6 A when it needs 8, it draws
    /// nothing at all — and a connected car taking no power is what the fast mode's completion dwell
    /// reads as <em>finished</em>. A charge that never started, filed as one that completed.</para>
    /// </summary>
    public int? MinChargingCurrentAmps { get; init; }

    /// <summary>
    /// The ceiling of the car's on-board charger. Null means "not stated".
    ///
    /// <para>Never raises the installation's ceiling — see <see cref="ChargingLimits"/>. A car that
    /// accepts 32 A behind a 16 A supply still charges at 16.</para>
    /// </summary>
    public int? MaxChargingCurrentAmps { get; init; }

    /// <summary>Where this car's own telemetry arrives, when a feed is configured.</summary>
    public EvTelemetryOptions? Telemetry { get; init; }
}

/// <summary>
/// The link between a car and the feed that reports on it.
///
/// <para>Only the topic lives here. The broker, the credentials, the staleness guard and the reconnect
/// interval stay in the <c>Vehicle</c> section, because they describe <b>the feed</b> rather than the
/// car — moving them would repeat the mistake this issue exists to fix. The topic is the one part that
/// is genuinely per-vehicle: two cars on one broker are two topics.</para>
/// </summary>
public sealed class EvTelemetryOptions
{
    public string Topic { get; init; } = string.Empty;
}
