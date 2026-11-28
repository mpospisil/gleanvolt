# Gleanvolt.Providers.Testing

Test a [Gleanvolt](https://github.com/mpospisil/gleanvolt) charger or car plugin without Gleanvolt. `ChargerTestHub`
is a charger hub on a loopback port, speaking the charger contract as Gleanvolt does. The plugin connects to it
with the ordinary SDK, so what is tested is what Gleanvolt will see.

```csharp
await using var hub = await ChargerTestHub.StartAsync();

services.AddGleanvoltChargerProvider<MyCharger>(options =>
{
    hub.Configure(options);          // the hub's address and token
    options.ProviderId = "my-charger";
    options.Name = "My charger";
});
// ... start the plugin's host ...

await hub.WaitForHelloAsync();
var reading = await hub.ReadAsync();                  // RefreshRequested, answered with Publish
Assert.Equal(ChargerState.Available, reading.Status);

var done = await hub.SetCurrentAsync(10);             // SetCurrent, answered with CommandDone
Assert.True(done.Succeeded);
```

- **`ReadAsync`** sends `RefreshRequested` and waits for the `Publish` that quotes it. A reading that breaks
  the contract's rules (`ChargerReadingRules`) is refused, as Gleanvolt refuses it, and `ReadAsync` throws a
  `ChargerContractException` that lists every problem.
- **`SetCurrentAsync`** and **`SetChargingAllowedAsync`** send a command and wait for its `CommandDone`.
- **`Readings`**, **`Refusals`**, **`Status`** and **`Hello`** keep everything the plugin sent, published
  unprompted or not.
- **The hello is checked as Gleanvolt checks it** (charger 1.1): the charger's `Capabilities` against the
  options' `Limits` (`ChargerLimitsRules`), refused with `limits` when they don't fit. The first plugin
  accepted is remembered, and another id is refused with `another-provider` until **`Forget()`**.
  **`LastRefusal`** says why a hello was refused.
- **Pairing** (#459): a plugin that starts with no token can pair with the hub. It answers Gleanvolt's two pairing
  endpoints (`PairingPaths`) and accepts every knock at once, collecting with the hub's own `Token` once the
  plugin proves the secret it knocked with. **`PairingKnocks`** lists what the plugin asked. Switch it off with
  `AcceptPairing = false` and the endpoints answer 404, as a Gleanvolt that predates pairing does.
- **`ChargerTestHubOptions`** sets the welcome (the charge control's limits, `DryRun`, the time zone), whether it accepts pairing, the
  token, how long to wait, and the clock the rules are checked against.

**A plugin on `Gleanvolt.Chargers.Hosting`** (#443) has two more helpers:

```csharp
await using var charger = new FakeModbusServer();              // a Modbus TCP charger: fill Holding / Input
charger.Input[0x001D] = 2;

await using var harness = new PluginHarness(plugin, new Dictionary<string, string?> { ["MyCharger:Host"] = $"127.0.0.1:{charger.Port}" });
var mine = new MyCharger(harness.Modbus(), TimeProvider.System);
var hub = await harness.ConnectAsync(mine, new ChargerLimits(6, 16, 3, 230));

var reading = await hub.ReadAsync();
await hub.SetCurrentAsync(10);
Assert.Equal(["0x0628=1000"], charger.Writes);
```

`ConnectAsync(..., paired: true)` starts the plugin with no token and no address, finds the hub and pairs with it,
the way a plugin installed before Gleanvolt does; afterwards `harness.Credentials` holds the paired token and
`harness.Pairing` the pairing's status. Its poll runs on a clock a hundred times faster than the SDK's 3 s.

`PluginHarness` gives the plugin its settings in a data directory of its own, the recorder and the session, and
`FakeClients(handler)` for an HTTP charger. `FakeModbusServer` keeps every write in order.

What it doesn't do: Gleanvolt's own safeguards (the setpoint threshold, the clamp to the charger's ceiling,
dry-run) are Gleanvolt's, not the plugin's, so they aren't here. Every command is passed on as asked.

## A car plugin

`VehicleTestHub` is the vehicle hub, on a loopback port, as Gleanvolt speaks it (#473). It welcomes the plugin with
the car and the charging session from `VehicleTestHubOptions`, keeps every reading and status, and refuses a reading
Gleanvolt would refuse (`Refusals`, with the reason, by the same rules: a capture time ahead of the clock or older than
30 days, a SOC outside 0–100, a reading that says nothing). `RefreshAsync` asks for a reading and returns the one in
reply; `SetCarAsync` and `SetSessionAsync` say what the owner or a charge changed.

```csharp
await using var harness = new CarPluginHarness(new PluginInfo("my-car", "My car", "MyCar", 8120));
var hub = await harness.ConnectAsync(new MyCar(...));

var reading = await hub.WaitForReadingAsync();
await hub.SetSessionAsync(new ChargingSessionInfo(true, DateTimeOffset.UtcNow, "SolarOnly"));
```

`CarPluginHarness` runs the car with its clock and its status reporter as `Gleanvolt.Cars.Hosting` wires them.
`StartAsync(car)` starts it with nowhere to connect, as a plugin before the owner accepts it; `ConnectAsync(car,
paired: true)` pairs it. `FakeClients(handler)` stands in for the car's maker.

MIT licence.
