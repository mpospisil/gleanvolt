# Implementation log

Reverse-chronological. Newest entry at the top.

---

## 2026-07-26 — Battery discharge hold (issue #20)

Branch: `feature/20-battery-discharge-hold`

A switch that stops the home battery discharging, so the EV charges from PV and grid while the
battery is still free to charge from PV surplus. Orthogonal to charge control: either can be on
without the other.

### What was built

**`Solax.Core`**

- `Enums/InverterControlRegister.cs` — the inverter's *holding* register space (a different address
  space from `InverterRegister`, which is input registers), carrying the Modbus Power Control block
  at `0x7C` and the "verify against your hardware" warning.
- `Enums/InverterPowerControlMode.cs`, `Enums/InverterPowerControlSetType.cs` — the device-level
  enums, with an explicit note on why there is no `No Discharge` value.
- `Strategies/BatteryDischargeHoldStrategy.cs` — pure, stateless computation of the `active_power`
  target: `-min(house load, PV)`.
- `Interfaces/IBatteryDischargeControl.cs`, `Interfaces/IBatteryHoldSelector.cs`,
  `Models/BatteryHoldState.cs`.
- `Models/EnergyState.HouseLoadPowerWatts` — total house load *including* the EV charger
  (`PV + Grid − Battery`), as distinct from the existing `OtherLoadsPowerWatts` residual, which
  excludes it. The hold needs the EV counted as load the grid may cover.

**`Solax.Infrastructure`**

- `RegisterMaps/PowerControlPayload.cs` — pure encoder for the 13-register block, 32-bit fields low
  word first.
- `BatteryDischargeControl.cs` — the write path on the keyed inverter client. Writes on arm, release,
  retarget beyond the threshold, and renewal; nothing in a steady state.

**`Solax.Worker`**

- `BatteryHoldSelector`, `Configuration/BatteryHoldOptions`, poll-loop reconciliation, and a Home
  Assistant `switch` plus **Battery power** and **Battery hold target** sensors.

### Architecture decisions

The central one — that the inverter has no `No Discharge` mode, so the hold is a computed
`Enabled Power Control` target rather than the fire-and-forget switch the issue described — is
recorded in [DECISIONS.md](DECISIONS.md) along with what it costs.

Two smaller ones worth noting here:

- **`BatteryHold:Enabled` is a real master switch, not a boot default.** `ChargeControl:Enabled`
  only seeds the mode, because Home Assistant can select `Solar` at runtime afterwards. That pattern
  can't hold here: issue #20 requires that `Enabled: false` performs no inverter writes *at all*, and
  a runtime-settable switch would break it. So while the flag is off the feature is entirely inert —
  no HA switch is published, the poll loop skips it, and the inverter's Modbus client is wrapped
  read-only. `HoldAtStartup` covers the boot value of the hold itself.
- **`WriteProofInDryRun` became `WriteProof`, taking an explicit `writable` flag.** It previously
  gated *both* device clients on `ChargeControl:DryRun`, which would have left the inverter writable
  whenever charge control was live — regardless of the battery-hold settings. Each device now derives
  writability from the feature that actually writes to it.

### Hardware quirks and edge cases

- **Holding register `0x7C` is overloaded**: written it is the power-control command, read it is the
  ARM firmware version. There is no read-back of the active command, so the HA switch reports what we
  last successfully wrote. A failed write therefore shows in HA as the switch returning to OFF rather
  than as an assumed success.
- **Nothing verified on hardware yet.** The register map comes from the upstream integration, not a
  SolaX document, and issue #20's Phase 0 observations are still outstanding. Hence
  `Enabled: false` + `DryRun: true` defaults.
- **The compensating check for the missing read-back**: if the battery is discharging while we
  believe the hold is armed (and we are not in dry-run), the poll loop logs a warning. It is the only
  observable signal that the command isn't taking effect on this firmware.
- **Clock going backwards** (NTP step, telemetry timestamp jitter) is treated as "renewal due" rather
  than deferring renewal indefinitely.
- A failed write is not recorded as armed, so the next poll retries instead of reporting a hold that
  was never established.

### Verification performed

- 131 unit tests pass, covering the payload encoding field by field, arm/release/retarget/renew/
  no-change, duration clamping, dry-run, failed-write retry, the target strategy, the selector, and
  the HA discovery configs and state payload.
- Smoke-run against the live inverter with `Enabled: true` + `DryRun: true`, confirming the encoded
  block is logged (`registers [1,1,0,0,0,0,60,0,0,0,0,0,0] at 0x7C`) and that no write reaches the
  device — the `ReadOnlyModbusClient` tripwire warning never fires.
