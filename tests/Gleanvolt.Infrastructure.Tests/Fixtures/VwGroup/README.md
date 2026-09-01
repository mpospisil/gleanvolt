# VW Group portal fixtures

What the parser tests in `VwGroupReportBundleTests` and `VwGroupVehicleStateMapperTests` run against.
No network anywhere in the suite — the discipline `VehicleTelemetryPayload` already follows, and the
reason that parser has stayed debuggable.

**JSON rather than committed ZIPs.** A `.zip` in the tree is opaque in a pull request: nobody can see
that a fixture changed, only that some bytes did. The tests build the archive from these files at run
time, which exercises the same `ZipArchive` path and leaves the fixtures reviewable.

**These are synthetic, and say so here rather than pretending otherwise.** They are shaped from what
issues #137 and #139 record of the portal — the flat `Data: [{key, dataFieldName, value}]` array, the
dotted (ID.x / MEB) and flat (older PHEV) layouts, several snapshots per download — and from the one
value this codebase has already written down as seen: `CHARGE_STATE_CHARGING_HV_BATTERY`.

**What that means for the field names.** The *structure* they exercise is real and the tie-break rules
they pin are the ones #139 specifies. The *spellings* in `VwGroupFieldNames` are inference, and the
first genuine download is what settles them. That download is cheap to get and cheap to act on:

```bash
dotnet run --project src/Gleanvolt.Worker -- vw-probe --save-fixture captured.json
```

writes a sanitised bundle — VIN, location and identifiers stripped — and the harness prints every
field the mapper did not recognise. Replace these files with it, add the names it reported, and the
tests stop being a rehearsal.
