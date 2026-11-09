namespace Gleanvolt.Devices.Testing;

/// <summary>
/// A recorded charger as the shared charger conformance tests see it (issue #352): the connection a driver is
/// created on, and what the driver wrote to it, described the same way whatever the transport.
/// </summary>
public interface IChargerReplay
{
    /// <summary>The connection over the recording.</summary>
    EvChargerConnection Connection { get; }

    /// <summary>Every write so far, oldest first, each described as one string a failure message can show.</summary>
    IReadOnlyList<string> Writes { get; }
}

/// <summary>A <see cref="ReplayModbusClient"/> as an <see cref="IChargerReplay"/>.</summary>
public sealed class ModbusChargerReplay(ReplayModbusClient client) : IChargerReplay
{
    /// <summary>The client, for the Modbus-only checks.</summary>
    public ReplayModbusClient Client { get; } = client;

    public EvChargerConnection Connection { get; } = EvChargerConnection.Over(client);

    public IReadOnlyList<string> Writes =>
        [.. Client.Writes.Select(write => $"{write.UnitId}:0x{write.Start:X4}={string.Join(',', write.Values)}")];
}

/// <summary>A <see cref="ReplayHttpHandler"/> as an <see cref="IChargerReplay"/>.</summary>
public sealed class HttpChargerReplay : IChargerReplay
{
    /// <param name="handler">The replay; the client this makes owns it.</param>
    public HttpChargerReplay(ReplayHttpHandler handler)
    {
        Handler = handler;
        Connection = EvChargerConnection.Over(new HttpClient(handler) { BaseAddress = new Uri("http://charger.test/") });
    }

    /// <summary>The handler, for the HTTP-only checks.</summary>
    public ReplayHttpHandler Handler { get; }

    public EvChargerConnection Connection { get; }

    public IReadOnlyList<string> Writes => Handler.Writes;
}
