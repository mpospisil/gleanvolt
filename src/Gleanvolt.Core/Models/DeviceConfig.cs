namespace Gleanvolt.Core.Models;

public sealed class DeviceConfig
{
    public required string Host { get; init; }

    public int Port { get; init; } = 502;

    public byte UnitId { get; init; } = 1;

    /// <summary>
    /// Minimum gap between consecutive Modbus requests to this device. The SolaX protocol asks for a
    /// pause between instructions; issuing them back to back makes the device reply late, and a late
    /// reply is what leaves an orphaned response in the socket buffer and desynchronises the stream
    /// (see <c>ModbusTcpClient</c> and issue #24).
    ///
    /// <para>250 ms is a deliberate compromise: the protocol documents a full second, but the poll
    /// loop makes four requests to the charger per cycle, which a 1 s gap would stretch across most of
    /// the poll interval. Raise it to <c>00:00:01</c> if desyncs persist on your hardware — recovery
    /// no longer depends on it, so this only affects how often the device is pushed into the state
    /// that requires recovering.</para>
    /// </summary>
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
