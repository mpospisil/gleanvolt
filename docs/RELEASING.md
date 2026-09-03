# Releasing

How a release is actually cut, as of the four-phase rework in [#144](https://github.com/mpospisil/gleanvolt/issues/144).

Read this before reaching for a tag. The obvious move — `git tag v1.2.3 && git push --tags` — does
**not** produce a release any more, and understanding why is most of understanding the rest.

## The version is not typed by anyone

Two halves, decided by two different parties.

| Half | Who decides | Where it lives |
|---|---|---|
| major and minor — the product line | a human, in a reviewed commit | `<MajorMinorVersion>` in `Directory.Build.props` |
| the build number | `release.yml` | the lowest number not already claimed by a `v<line>.*` tag |

So the next release number is knowable without running anything: read the product line, list the
tags. The `version` job prints it as the first thing it does — *"Product line 1.0, next free build
0"*.

A build with no build number passed to it is stamped `-dev` and is self-evidently local:
`1.0.0-dev`. That is why a version ending in `-dev` in a log or on a device page means nobody
released it.

**To move to a new line**, edit `<MajorMinorVersion>` and both Dockerfiles' `ARG VERSION` default in
the same commit. `DockerfileTests` fails if you change one and not the others; that is what it is
for.

## The tag is an output, not a trigger

`release.yml` is `workflow_dispatch` only. It builds, tests, packs, publishes and smoke-runs
*first*, and only then tags the commit with the number it just used. A tag therefore cannot name a
build that does not exist.

There is deliberately no `push: tags` trigger. It would be a second entrance that derives a version
from a name someone typed, which is the drift the build number exists to remove.

## Cutting one

### 1. Prove it, creating nothing

```
gh workflow run release.yml --ref main -f release=false
```

Four jobs — `version`, `build`, `publish` (three legs), `release` (skipped) — and every one of them
runs exactly as it would for a real release. The three publish legs each build on the platform they
name and then **start the binary they just made**, asserting it survives an unreachable inverter,
answers `/api/v1/health`, serves `/_framework/blazor.web.js` non-empty, and reports the version this
run built. Artifacts are uploaded; no tag, no release, nothing permanent.

A dry run stamps `-dev` onto the number it *would* have used, so `1.0.5-dev` proves build 5 without
producing a filename that claims a release.

Do this after any change to the workflow. It costs nothing.

### 2. Cut it

```
gh workflow run release.yml --ref main -f release=true
```

Same four jobs, and this time the `release` job runs: it collects every artifact, checks the three
zips and five `.nupkg` all arrived, writes `SHA256SUMS`, attests build provenance, pushes the tag,
and creates the GitHub Release.

The tag push is the backstop under the whole scheme — it is rejected if the tag already exists, so
two runs that somehow chose the same number cannot both publish it.

### 3. The image

`publish-image.yml` triggers on a `v*` tag. **Whether step 2 starts it depends on one secret.**

- **With `RELEASE_TAG_TOKEN`** (a PAT with `contents: write`), the tag is pushed as a person and the
  image workflow starts on its own, carrying the same number.
- **Without it**, the tag is pushed with `GITHUB_TOKEN`, and GitHub does not start a workflow from a
  push made with it. No image is built. The run summary says so and gives the command:

  ```
  gh workflow run publish-image.yml --ref v1.0.0
  ```

The run summary tells you which of the two happened. Believe it rather than assuming.

### 4. Behave like a stranger

The smoke tests prove the binary starts. They cannot prove the *page* works. From the releases page,
in a browser, on a machine that has never had .NET installed:

- Download `gleanvolt-<version>-win-x64.zip`, unzip it, run it, open the UI.
- Repeat on the Pi with `linux-arm64`.
- Check that the notes say what to download, and that following the README's install section lands
  somewhere.

This is the half a workflow cannot do for you, and it is the half a stranger meets first.

## What a release carries

| | |
|---|---|
| `gleanvolt-<version>-win-x64.zip` | self-contained, ~51 MB |
| `gleanvolt-<version>-linux-x64.zip` | self-contained, ~48 MB |
| `gleanvolt-<version>-linux-arm64.zip` | self-contained, ~46 MB — this is the Pi |
| five `.nupkg` and five `.snupkg` | the libraries and their symbols, attached rather than pushed to a feed |
| `SHA256SUMS` | covers every file above, and not itself |

Each zip and `.nupkg` also carries a build provenance attestation, verifiable with
`gh attestation verify <file> --repo mpospisil/gleanvolt` — which says *which run and which commit*
produced it, without trusting the release page.

Self-contained means the runtime ships inside: none of the three needs .NET installed. Trimming is
off on purpose — the configuration binder and the options types are resolved reflectively, and a
trimmed build fails at startup rather than at compile time.

## Prereleases

A hyphen anywhere in the version makes `gh release create` pass `--prerelease`, so an `-rc` cannot
take `latest`. The build numbers this workflow derives never contain one, so this is a guard on the
product line rather than a routine path.

## The two workflows are not synchronised

`release.yml` and `publish-image.yml` are independent. A green release beside a failed image
manifest is reachable. If it happens, that is a follow-up to fix — **not** a reason to delete the
tag, which is the one thing in this process that cannot be taken back.
