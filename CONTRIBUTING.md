# Contributing

Bug reports, hardware observations and register-map corrections are the most valuable things you can
send. This project talks to real inverters whose firmware varies between generations, and a report of
"register 0x25 reads differently on my X3-HYB-G4 with firmware N" is worth more than most code.

## Before you open a pull request

**Sign off your commits.** Every commit needs a `Signed-off-by` line, which `git commit -s` adds:

```bash
git commit -s -m "Your message"
```

That line means you have read and agreed to the [Contributor Licence Agreement](CLA.md). It matters
here more than in most projects: the licence offers commercial use separately, and that is only
possible if one person can license the whole codebase. See [CLA.md](CLA.md) for the reasoning — you
keep your copyright either way.

## What the code expects

- **Read [`docs/DECISIONS.md`](docs/DECISIONS.md) first** if you are changing anything structural. It
  records why things are the way they are, and several of the answers are non-obvious.
- **Respect the layering.** `Gleanvolt.Worker` → `Gleanvolt.Hosting` → `Gleanvolt.Infrastructure` → `Gleanvolt.Core`,
  one way only. Decision logic lives in `Gleanvolt.Core`, expressed against interfaces, so it stays
  testable with no hardware. The rules are listed in the README.
- **Comments explain *why*, not *what*.** The existing ones are dense on purpose: most of them record
  a fact about the hardware, a failure that was actually observed, or an alternative that was tried
  and rejected. Match that, and prefer adding the reason to restating the code.
- **Tests come with the change.** `dotnet test Gleanvolt.slnx` must pass. Control logic is
  tested against mocked `IModbusClient`, never a live device.

## Anything that writes to hardware

`ChargeControl` and `BatteryHold` are the two features that write — to the EV charger and to the
inverter respectively. Both ship disabled, and `BatteryHold` additionally defaults to dry-run,
because a wrong register address on a hybrid inverter is not a failing test but a house with no power.

If your change touches a write path:

- Say in the pull request **which hardware and firmware you verified it against**, or say plainly that
  you did not.
- Keep the defaults off. A feature that writes must be something the operator switched on knowingly.
- Do not widen what a `ReadOnlyModbusClient` protects. The dry-run guarantee is structural — a client
  that may not write physically cannot — and it should stay that way.

## Cutting a release

Not something a pull request does, and not something a tag does either: `release.yml` is dispatched,
and it writes the tag itself once everything it built has been started and passed. See
[`docs/RELEASING.md`](docs/RELEASING.md) before reaching for `git tag`.

## Reporting a problem instead

An issue with the controller's log around the event, the device model and its firmware version, and
what you expected to happen is a complete report. Redact your Solcast key and any broker credentials
before pasting a log.
