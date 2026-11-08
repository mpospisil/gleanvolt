using Gleanvolt.Devices;

namespace Gleanvolt.Hosting.Setup;

/// <summary>
/// The Modbus client setup mode registers in place of the real ones (#301): there is no confirmed
/// address to connect to, so every call refuses. Nothing in setup mode should get this far -- the
/// workers that poll are not running -- and if something does, it fails loudly rather than opening a
/// socket to an address nobody chose.
/// </summary>
internal sealed class NotConfiguredModbusClient : IModbusClient
{
    public bool IsConnected => false;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Refuse();

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        Refuse<ushort[]>();

    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        Refuse<ushort[]>();

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) => Refuse();

    public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) => Refuse();

    // Every unit refuses alike.
    public IModbusClient ForUnit(byte unitId) => this;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task Refuse() => Task.FromException(Error());

    private static Task<T> Refuse<T>() => Task.FromException<T>(Error());

    private static InvalidOperationException Error() =>
        new("Gleanvolt is in setup mode: no inverter or charger is configured, so no Modbus call is made.");
}
