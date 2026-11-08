namespace Gleanvolt.Devices;

public interface IModbusClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<ushort[]> ReadHoldingRegistersAsync(
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default);

    Task<ushort[]> ReadInputRegistersAsync(
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default);

    Task WriteSingleRegisterAsync(
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default);

    Task WriteMultipleRegistersAsync(
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same connection, addressed to another Modbus unit id (issue #322): for a gateway that answers
    /// for itself on one unit and forwards to a device behind it on another, and accepts one client only,
    /// as Huawei's EMMA does. The view shares the connection, its pacing and its one-request-at-a-time
    /// rule; disposing it does not close the connection. A client that cannot address another unit
    /// throws <see cref="NotSupportedException"/>.
    /// </summary>
    IModbusClient ForUnit(byte unitId) =>
        throw new NotSupportedException($"{GetType().Name} cannot address another unit id.");
}
