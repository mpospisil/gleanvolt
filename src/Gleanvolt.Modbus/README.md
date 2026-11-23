# Gleanvolt.Modbus

The Modbus TCP client [Gleanvolt](https://github.com/mpospisil/gleanvolt) and its charger plugins talk to
devices with, built on [NModbus](https://github.com/NModbus/NModbus). It keeps a connection usable on
devices that answer late, drop the link or take one client at a time:

- **One request at a time**, spaced by `ModbusEndpoint.MinRequestInterval`. Many devices start
  answering late when asked too quickly.
- **Any failed exchange tears the connection down,** and the next request reconnects. A reply that
  arrives after its request gave up would otherwise be read as the answer to the next one, and every
  request after it would stay one reply behind.
- **A reply older than its request is read past** instead of failing the request.
- **A reconnect backoff** (`ModbusEndpoint.ReconnectBackoff`), doubling up to 2 minutes, for a gateway
  that recovers slowly when it is connected to on every poll.
- **A device's refusal is a `ModbusDeviceException`** with its exception code, so "nothing at this
  address" can be told from a dead link without knowing NModbus.
- **`ForUnit(unitId)`** addresses another unit id over the same connection, for a gateway that takes
  a single client.

```csharp
await using var client = new ModbusTcpClient(new ModbusEndpoint("192.168.1.6") { UnitId = 1 });
var registers = await client.ReadInputRegistersAsync(0x0000, 10);
```

Write against `IModbusClient`, so a test can hand the driver a recording instead of a device.

MIT licence.
