# Implementation log

Reverse-chronological. Newest entry at the top.

---

## 2026-07-29 — Modbus reconnect on failure (issue #24)

Branch: `fix/24-modbus-reconnect`

A three-hour run produced 43 successful polls and 564 transaction-ID errors. Bucketed by ten minutes
it is not intermittent at all — it is a cliff:

| bucket | ok | errors |
|---|---|---|
| 18:50 | 4 | 0 |
| 19:00 | 39 | 10 |
| 19:10 | **0** | 91 |
| 19:20–20:00 | **0** | ~97 each |

### Cause

A late response left in the socket buffer puts the stream permanently one or more replies behind.
NModbus's own retries cannot escape it, and nothing tears the connection down — `IsConnected` is true
because the socket is fine. See [DECISIONS.md](DECISIONS.md).

### What changed

`ModbusTcpClient` rewritten around a single `ExecuteAsync` path: serialise on a `SemaphoreSlim`,
connect on demand, wait out `DeviceConfig.MinRequestInterval`, run the operation, and — on any failure
— invalidate the connection through an exception filter so the original exception still surfaces
unchanged. `DeviceConfig` gained `MinRequestInterval` (250 ms default).

### Things worth knowing

- **NModbus retries a mismatched response by re-sending the request**, so corrupting a single reply is
  self-healing and proves nothing. The tests had to model the *persistent* offset to reproduce the
  field failure — that discovery is baked into `FakeModbusTcpServer.Persistent`.
- **The tests need a real socket.** `FakeModbusTcpServer` implements enough MBAP framing for function
  codes 3, 4, 6 and 16, plus deliberate transaction-id corruption.
- **Verified as regression tests**: disabling only the invalidation makes 3 of the 9 fail; restoring it
  makes all 9 pass.
- **Not verified on hardware yet** — the fix wants an overnight run showing successful polls all the
  way to the end of the log.

### Files

`src/Solax.Infrastructure/Modbus/ModbusTcpClient.cs`, `src/Solax.Core/Models/DeviceConfig.cs`,
`tests/Solax.Infrastructure.Tests/{ModbusTcpClientTests,FakeModbusTcpServer}.cs`, docs.

---

## 2026-07-27 — Battery hold verified on hardware; discharge deadband; grid-power sensor (issue #20)

Branch: `feature/20-battery-discharge-hold`

Follow-up to the entry below, which shipped the hold unverified. It has now been exercised against
the live inverter.

### What was found

The mechanism works. Arming the hold at dusk (PV ~360 W, SOC 87 %, no EV charging) moved the house
off the battery within a single poll: battery **−2846 W → −56 W**, grid **0 W → +1601 W**, solar
unchanged at ~370 W. Renewal at half the duration held it continuously with no observed lapse, and PV
was not curtailed. Full measurements are in [DECISIONS.md](DECISIONS.md).

### What changed

- **A 150 W deadband on the "hold armed but battery discharging" warning**
  (`SolaxPollingService.ResidualDischargeWatts`). A working hold still leaves a **50–65 W trickle**
  out of the battery — inverter standby draw, not load being served; it persisted while house load
  swung between 143 W and 2877 W. The warning originally fired on any negative value, so it fired
  every poll and drowned out the signal it existed to give.
- **Grid power exposed as a Home Assistant sensor** (`grid_w` in the state payload, `grid_power`
  discovery config), so the hold can be observed from HA rather than only from the log — watching
  import rise as battery discharge falls is the clearest evidence the hold is working.

### Consequences

- **Issue #20's "`BatteryPowerWatts` is never negative" acceptance criterion is not literally
  achievable** on this hardware, because of the standby trickle. The achievable guarantee is that the
  battery stops *serving house load*.
- Defaults are unchanged: `Enabled: false`, `DryRun: true`. Verification on one inverter and firmware
  says nothing about any other.

### Still unobserved

Behaviour under strong midday PV (does the battery still charge from surplus while held, and is PV
curtailed at full output), behaviour with the EV actually charging, and what the undocumented
`timeout` field does relative to `duration`.

134 unit tests pass.

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
  `Enabled: false` + `DryRun: true` defaults. *(Superseded — see the 2026-07-27 entry above: the hold
  was subsequently verified on the reference inverter. The defaults stand regardless.)*
- **The compensating check for the missing read-back**: if the battery is discharging while we
  believe the hold is armed (and we are not in dry-run), the poll loop logs a warning. It is the only
  observable signal that the command isn't taking effect on this firmware. *(A 150 W deadband was
  added on 2026-07-27; a working hold still trickles 50–65 W.)*
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
