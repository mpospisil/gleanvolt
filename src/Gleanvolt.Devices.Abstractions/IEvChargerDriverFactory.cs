namespace Gleanvolt.Devices;

/// <summary>
/// Makes the <see cref="IEvChargerDriver"/> for one charger family, as <see cref="IInverterDriverFactory"/>
/// does for inverters. The host registers one per family and picks by <see cref="Id"/>.
/// </summary>
public interface IEvChargerDriverFactory
{
    /// <summary>The family's id in configuration, lower case: <c>solax</c>.</summary>
    string Id { get; }

    /// <summary>The brand as an owner knows it, for a drop-down: <c>SolaX</c>. A name, so never translated.</summary>
    string DisplayName { get; }

    /// <summary>How the family's charger is reached, so the host knows which connection to build.</summary>
    EvChargerTransport Transport { get; }

    /// <summary>
    /// How the family's Modbus server is reached when the configuration does not say; null for a family
    /// reached over HTTP.
    /// </summary>
    ModbusDefaults? Defaults { get; }

    /// <summary>What the family can do, known before a driver exists. Every driver this makes reports the same.</summary>
    EvChargerCapabilities Capabilities { get; }

    /// <summary>
    /// What a register snapshot of this family reads (issue #350): at least every register the driver reads,
    /// and where the serial number is. Null for a family not reached over Modbus.
    /// </summary>
    RegisterSnapshotSpec? Snapshot { get; }

    /// <summary>
    /// A driver on <paramref name="connection"/>. The connection belongs to the host, which opens and disposes
    /// it; in dry-run it is one that refuses writes. Throws when it is not of this family's
    /// <see cref="Transport"/>.
    /// </summary>
    IEvChargerDriver Create(EvChargerConnection connection, EvChargerDriverOptions options);
}

/// <summary>
/// What is known about a charger driver without creating one (issue #349): enough to check a configured id
/// and the charge-control limits against what the family can do, name the brand in a drop-down, and fill in
/// an address the configuration left incomplete. As <see cref="InverterDriverInfo"/> is for inverters.
/// </summary>
/// <param name="Id">See <see cref="IEvChargerDriverFactory.Id"/>.</param>
/// <param name="DisplayName">See <see cref="IEvChargerDriverFactory.DisplayName"/>.</param>
/// <param name="Transport">See <see cref="IEvChargerDriverFactory.Transport"/>.</param>
/// <param name="Defaults">See <see cref="IEvChargerDriverFactory.Defaults"/>; null for a family reached over HTTP.</param>
/// <param name="Capabilities">See <see cref="IEvChargerDriverFactory.Capabilities"/>.</param>
public sealed record EvChargerDriverInfo(
    string Id,
    string DisplayName,
    EvChargerTransport Transport,
    ModbusDefaults? Defaults,
    EvChargerCapabilities Capabilities)
{
    /// <summary>The description of <paramref name="factory"/>.</summary>
    public static EvChargerDriverInfo Of(IEvChargerDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new EvChargerDriverInfo(factory.Id, factory.DisplayName, factory.Transport, factory.Defaults, factory.Capabilities);
    }
}

/// <summary>How a charger family is reached.</summary>
public enum EvChargerTransport
{
    /// <summary>Modbus TCP: <see cref="EvChargerConnection.Modbus"/>.</summary>
    Modbus,

    /// <summary>A local HTTP API: <see cref="EvChargerConnection.Http"/>.</summary>
    Http,
}

/// <summary>
/// The connection a charger driver is handed (issue #347, decision 2): Modbus for most families, HTTP for
/// those whose documented local interface is a web API (go-e). Exactly one is set. The host owns both.
/// </summary>
public sealed record EvChargerConnection
{
    private EvChargerConnection(IModbusClient? modbus, HttpClient? http)
    {
        Modbus = modbus;
        Http = http;
    }

    /// <summary>The Modbus client, for <see cref="EvChargerTransport.Modbus"/>.</summary>
    public IModbusClient? Modbus { get; }

    /// <summary>
    /// The HTTP client, with its base address set to the charger, for <see cref="EvChargerTransport.Http"/>.
    /// </summary>
    public HttpClient? Http { get; }

    /// <summary>Which of the two this is.</summary>
    public EvChargerTransport Transport => Modbus is not null ? EvChargerTransport.Modbus : EvChargerTransport.Http;

    /// <summary>A connection over <paramref name="client"/>.</summary>
    public static EvChargerConnection Over(IModbusClient client) =>
        new(client ?? throw new ArgumentNullException(nameof(client)), null);

    /// <summary>A connection over <paramref name="client"/>, whose base address is the charger.</summary>
    public static EvChargerConnection Over(HttpClient client) =>
        new(null, client ?? throw new ArgumentNullException(nameof(client)));

    /// <summary>The Modbus client, or an exception that names the family which needed it.</summary>
    public IModbusClient RequireModbus(string family) =>
        Modbus ?? throw new ArgumentException($"The {family} charger is reached over Modbus, not over {Transport}.");
}

/// <summary>What the host tells a charger driver when it creates one.</summary>
public sealed record EvChargerDriverOptions
{
    /// <summary>
    /// Log the writes the driver would make instead of making them, and let a value it would have written
    /// stand in for the device's when it is read back. The host also hands the driver a connection that
    /// refuses writes.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>How far the current setpoint has to move before it is written again; at least 1 A.</summary>
    public int CurrentChangeThresholdAmps { get; init; } = 1;

    /// <summary>
    /// The phases the car charges on (<c>ChargeControl:Phases</c>), for a family whose setpoint is power rather
    /// than current (Huawei's SCharger, issue #351). A one-phase car on a three-phase charger draws on one, so
    /// its amps convert at one phase. Null means every phase the family supplies.
    /// </summary>
    public int? Phases { get; init; }

    /// <summary>The voltage per phase that amps and watts convert at (<c>ChargeControl:NominalVoltage</c>).</summary>
    public double NominalVoltage { get; init; } = 230;
}
