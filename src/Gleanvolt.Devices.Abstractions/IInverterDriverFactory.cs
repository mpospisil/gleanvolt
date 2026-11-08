namespace Gleanvolt.Devices;

/// <summary>
/// Makes the <see cref="IInverterDriver"/> for one inverter family. The host registers one per family and
/// picks by <see cref="Id"/>; the configuration only ever names the id.
/// </summary>
public interface IInverterDriverFactory
{
    /// <summary>The family's id in configuration (<c>Pv:Inverter:Driver</c>), lower case: <c>solax</c>.</summary>
    string Id { get; }

    /// <summary>The brand as an owner knows it, for a drop-down: <c>SolaX</c>. A name, so never translated.</summary>
    string DisplayName { get; }

    /// <summary>How this family's Modbus server is reached when the configuration does not say.</summary>
    ModbusDefaults Defaults { get; }

    /// <summary>
    /// What the family can do, known before a driver exists, so a configuration that asks for more can be
    /// refused when it is saved. Every driver this makes reports the same.
    /// </summary>
    InverterCapabilities Capabilities { get; }

    /// <summary>
    /// What a register snapshot of this family reads (issue #319): at least every register the driver reads,
    /// so the snapshot replays it, and where the serial number is, so it can be removed.
    /// </summary>
    RegisterSnapshotSpec Snapshot { get; }

    /// <summary>
    /// How the family's inverter can be reached, when there is more than one way (issue #322): through a
    /// dongle, or behind an energy manager that answers for the grid itself. The first is the default, the
    /// one an empty <c>Pv:Inverter:Topology</c> means. Empty for a family reached one way only.
    /// </summary>
    IReadOnlyList<InverterTopology> Topologies => [];

    /// <summary>
    /// What a snapshot reads for a driver made with <paramref name="options"/>: <see cref="Snapshot"/>,
    /// unless the topology changes what the driver reads.
    /// </summary>
    RegisterSnapshotSpec SnapshotFor(InverterDriverOptions options) => Snapshot;

    /// <summary>
    /// A driver on <paramref name="client"/>. The client belongs to the host, which connects and disposes
    /// it; in dry-run it is one that refuses writes.
    /// </summary>
    IInverterDriver Create(IModbusClient client, InverterDriverOptions options);
}

/// <summary>
/// What is known about a driver without creating one: enough to check a configured id, name the brand in a
/// drop-down and fill in an address the configuration left incomplete.
/// </summary>
/// <param name="Id">See <see cref="IInverterDriverFactory.Id"/>.</param>
/// <param name="DisplayName">See <see cref="IInverterDriverFactory.DisplayName"/>.</param>
/// <param name="Defaults">See <see cref="IInverterDriverFactory.Defaults"/>.</param>
/// <param name="Capabilities">See <see cref="IInverterDriverFactory.Capabilities"/>.</param>
/// <param name="Topologies">See <see cref="IInverterDriverFactory.Topologies"/>; null is none.</param>
public sealed record InverterDriverInfo(
    string Id,
    string DisplayName,
    ModbusDefaults Defaults,
    InverterCapabilities Capabilities,
    IReadOnlyList<InverterTopology>? Topologies = null)
{
    /// <summary>The ways the inverter can be reached; empty when there is one.</summary>
    public IReadOnlyList<InverterTopology> Topologies { get; init; } = Topologies ?? [];

    /// <summary>The description of <paramref name="factory"/>.</summary>
    public static InverterDriverInfo Of(IInverterDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new InverterDriverInfo(factory.Id, factory.DisplayName, factory.Defaults, factory.Capabilities, factory.Topologies);
    }

    // The topologies by content, so two descriptions of one factory are equal.
    public bool Equals(InverterDriverInfo? other) =>
        other is not null
        && Id == other.Id
        && DisplayName == other.DisplayName
        && Defaults == other.Defaults
        && Capabilities == other.Capabilities
        && Topologies.SequenceEqual(other.Topologies);

    public override int GetHashCode() => HashCode.Combine(Id, DisplayName, Defaults, Capabilities, Topologies.Count);
}

/// <summary>One way an inverter family can be reached (issue #322).</summary>
/// <param name="Id">The id in configuration (<c>Pv:Inverter:Topology</c>), lower case: <c>emma</c>.</param>
/// <param name="DisplayName">What the owner knows it as: <c>EMMA</c>. A product name, so never translated.</param>
public sealed record InverterTopology(string Id, string DisplayName);

/// <summary>A family's Modbus defaults.</summary>
/// <param name="Port">The TCP port.</param>
/// <param name="UnitId">The Modbus unit (slave) id.</param>
/// <param name="MinRequestInterval">The pause the family needs between two requests.</param>
/// <param name="ReconnectBackoff">
/// How long to wait before connecting again after a connect failed, doubling with each further failure;
/// zero reconnects on the next request. For a gateway that takes few clients and recovers slowly (Huawei's
/// SDongle), where a reconnect every poll keeps it from recovering at all.
/// </param>
public sealed record ModbusDefaults(int Port, byte UnitId, TimeSpan MinRequestInterval, TimeSpan ReconnectBackoff = default);

/// <summary>What the host tells a driver when it creates one.</summary>
public sealed record InverterDriverOptions
{
    /// <summary>
    /// Log the writes the battery hold would make instead of making them. The host also hands the driver a
    /// client that refuses writes, so this is about what the log says, not about safety.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// How long one battery hold command lasts before it lapses on its own, for a family whose hold is a
    /// command with a duration. Null leaves it to the driver.
    /// </summary>
    public TimeSpan? HoldCommandDuration { get; init; }

    /// <summary>How far the hold's target has to move before the command is sent again.</summary>
    public double HoldRetargetThresholdWatts { get; init; } = 100;

    /// <summary>
    /// How the inverter is reached: one of the factory's <see cref="IInverterDriverFactory.Topologies"/> by
    /// id, lower case. Empty is the factory's first, or the only way for a family with none.
    /// </summary>
    public string Topology { get; init; } = string.Empty;
}
