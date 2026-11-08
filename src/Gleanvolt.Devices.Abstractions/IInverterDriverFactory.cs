namespace Gleanvolt.Devices;

/// <summary>
/// Makes the <see cref="IInverterDriver"/> for one inverter family. The host registers one per family and
/// picks by <see cref="Id"/>; the configuration only ever names the id.
/// </summary>
public interface IInverterDriverFactory
{
    /// <summary>The family's id in configuration, lower case: <c>solax</c>.</summary>
    string Id { get; }

    /// <summary>How this family's Modbus server is reached when the configuration does not say.</summary>
    ModbusDefaults Defaults { get; }

    /// <summary>
    /// A driver on <paramref name="client"/>. The client belongs to the host, which connects and disposes
    /// it; in dry-run it is one that refuses writes.
    /// </summary>
    IInverterDriver Create(IModbusClient client, InverterDriverOptions options);
}

/// <summary>A family's Modbus defaults.</summary>
/// <param name="Port">The TCP port.</param>
/// <param name="UnitId">The Modbus unit (slave) id.</param>
/// <param name="MinRequestInterval">The pause the family needs between two requests.</param>
public sealed record ModbusDefaults(int Port, byte UnitId, TimeSpan MinRequestInterval);

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
}
