# VW Group portal fixtures

What the parser tests in `VwGroupReportBundleTests` and `VwGroupVehicleStateMapperTests` run against.
No network anywhere in the suite — the discipline `VehicleTelemetryPayload` already follows, and the
reason that parser has stayed debuggable.

**JSON rather than committed ZIPs.** A `.zip` in the tree is opaque in a pull request: nobody can see
that a fixture changed, only that some bytes did. The tests build the archive from these files at run
time, which exercises the same `ZipArchive` path and leaves the fixtures reviewable.

**`id4-live-capture.json` is real** — downloaded from the portal for the reference ID.4 on
2026-09-01 and sanitised (VIN and user id replaced). It is what `VwGroupLiveCaptureTests` runs
against, and it corrected four field names the synthetic ones had wrong. The rest below are still
synthetic.

**The others are synthetic, and say so here rather than pretending otherwise.** They are shaped from what
issues #137 and #139 record of the portal — the flat `Data: [{key, dataFieldName, value}]` array, the
dotted (ID.x / MEB) and flat (older PHEV) layouts, several snapshots per download — and from the one
value this codebase has already written down as seen: `CHARGE_STATE_CHARGING_HV_BATTERY`.

**What that means for the field names.** The *structure* they exercise is real and the tie-break rules
they pin are the ones #139 specifies. The *spellings* in `VwGroupFieldNames` are inference, and only a
genuine download settles them.

**There is no longer a capture command.** The `vw-probe` console harness that wrote a sanitised bundle
with `--save-fixture` was removed once the web UI's **Vehicle portal** page took over the job of
proving a sign-in; it is recoverable from git history (`src/Gleanvolt.Worker/VwProbe.cs`, commit
`27e17ab`) if the sanitiser is wanted back.

What the page does still give, and what matters most day to day, is **the list of field names nothing
here recognised** in the bundle it just downloaded. Add those to `VwGroupFieldNames` and a blank SOC
or range usually stops being blank. Replacing these synthetic files with a real capture needs the
sanitiser restored first — nothing should commit an unsanitised bundle, which carries the VIN.

Credentials for the page, and the four browser steps the portal needs before it will deliver anything,
are documented in the repo root's `.env.example` and walked through in `docs/VW_PORTAL_SETUP.md`.
