namespace Gleanvolt.Modbus;

/// <summary>
/// Where a Modbus TCP device is, and how it wants to be talked to: what <see cref="ModbusTcpClient"/> needs and
/// nothing else.
/// </summary>
/// <param name="Host">The device's host name or IP address.</param>
public sealed record ModbusEndpoint(string Host)
{
    /// <summary>The longest <see cref="ReconnectBackoff"/> grows to.</summary>
    public static TimeSpan MaxReconnectBackoff { get; } = TimeSpan.FromMinutes(2);

    /// <summary>The TCP port; 502 by default.</summary>
    public int Port { get; init; } = 502;

    /// <summary>The Modbus unit (slave) id; 1 by default.</summary>
    public byte UnitId { get; init; } = 1;

    /// <summary>
    /// The least time between the end of one request and the start of the next; 250 ms by default. A device asked
    /// too quickly starts replying late, which is how a stream ends up behind.
    /// </summary>
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long to wait before connecting again after a failed connect, doubling with each further failure, up to
    /// <see cref="MaxReconnectBackoff"/>. A connect that succeeds starts it over. Zero, the default, connects on
    /// every request.
    /// </summary>
    public TimeSpan ReconnectBackoff { get; init; } = TimeSpan.Zero;
}
