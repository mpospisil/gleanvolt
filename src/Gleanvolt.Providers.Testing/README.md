# Gleanvolt.Providers.Testing

Test a [Gleanvolt](https://github.com/mpospisil/gleanvolt) charger plugin without Gleanvolt. `ChargerTestHub`
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
- **`ChargerTestHubOptions`** sets the welcome (the charge control's limits, `DryRun`, the time zone), the
  token, how long to wait, and the clock the rules are checked against.

What it doesn't do: Gleanvolt's own safeguards (the setpoint threshold, the clamp to the charger's ceiling,
dry-run) are Gleanvolt's, not the plugin's, so they aren't here. Every command is passed on as asked.

MIT licence.
