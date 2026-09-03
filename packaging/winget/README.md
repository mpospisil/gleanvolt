# Publishing to WinGet

`winget install MartinPospisil.Gleanvolt` needs a package to exist in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). Getting there is two different
jobs, and only the second one is automated.

## The first version is a manual pull request. This is not optional.

`winget-releaser`'s very first step is a check that the package already exists, and it fails the run
if it does not — in its own words:

> `Package MartinPospisil.Gleanvolt does not exist in the winget-pkgs repository. Please add atleast
> one version of the package before using this action.`

The action only ever *updates*. Under the hood it runs `komac update … --submit`, which copies the
previous version's manifest forward with a new URL, version and hash. So the **shape** of the
package — that it is a zip containing a portable executable, and which executable — is decided once,
here, by hand, and inherited by every release afterwards. Getting it right matters more than it
looks.

### Prerequisites

| | |
|---|---|
| A fork of `microsoft/winget-pkgs` | on the account named by `fork-user` (the repository owner). `komac sync-fork` fails without it. |
| `WINGET_TOKEN` | a **classic** PAT with `public_repo` scope, on that same account. Not `GITHUB_TOKEN`: the pull request is opened against another organisation's repository. |

### Submitting

The manifests in `manifests/` are a worked example pinned to the version they were written for. Check
that the `InstallerUrl` still resolves before submitting: a release can be deleted and re-cut, and a
manifest pointing at an asset that no longer exists is rejected by winget-pkgs' automated validation
rather than by anything here.


1. Refresh the three fields in `manifests/<version>/` that are version-specific. Everything else —
   identifier, publisher, licence, description, tags, and the nested-installer layout — carries
   forward unchanged.

   | File | Field | Where it comes from |
   |---|---|---|
   | all three | `PackageVersion` | the release tag without its `v` |
   | installer | `InstallerUrl` | the release asset's download URL |
   | installer | `InstallerSha256` | the release's own `SHA256SUMS`, upper-cased |
   | installer | `ReleaseDate` | the release's publication date, quoted |
   | locale | `ReleaseNotesUrl` | the release page |

   The hash is already published with every release, so it need not be recomputed:

   ```
   gh release download <tag> --repo mpospisil/gleanvolt --pattern SHA256SUMS
   grep win-x64 SHA256SUMS | cut -d' ' -f1 | tr 'a-f' 'A-F'
   ```

2. Copy the three files into your winget-pkgs fork at
   `manifests/m/MartinPospisil/Gleanvolt/<version>/`, and open a pull request against
   `microsoft/winget-pkgs`.

3. Validate before submitting, if you are on Windows:

   ```
   winget validate --manifest manifests\m\MartinPospisil\Gleanvolt\<version>
   ```

   Elsewhere, the manifests carry `$schema` lines, so any YAML language server with schema support
   checks them as you type.

4. A Microsoft reviewer merges it. Automated validation installs the package on a clean machine, so
   an installer URL that 404s or a hash that does not match is rejected there rather than by anyone
   here.

Once that pull request is merged, `.github/workflows/publish-winget.yml` takes over and no further
manual step exists.

## Why the manifest looks the way it does

**`InstallerType: zip` with `NestedInstallerType: portable`.** The release ships a self-contained
zip, not an installer. It needs no .NET, writes nothing outside its own directory, and has nothing to
uninstall, which is exactly what WinGet calls a portable.

**`RelativeFilePath: win-x64\Gleanvolt.Worker.exe`.** The zip contains exactly one top-level
directory, named for the runtime identifier. That is deliberate in `release.yml` — it is why the zip
is created from inside `publish/` rather than from its contents — and this path depends on it. The
release workflow's own smoke test unpacks the zip and starts
`unpacked/win-x64/Gleanvolt.Worker.exe`, so anything that broke this layout would fail the release
before it could reach WinGet.

**`PortableCommandAlias: gleanvolt`.** What the command is called once WinGet has put it on `PATH`.

**The licence is stated as it is.** PolyForm Noncommercial 1.0.0, with `LicenseUrl` pointing at the
repository's own `LICENSE`. WinGet publishes proprietary and source-available software alike; what
it requires is that the field says what is true.

## Updating an existing version's manifest

Don't, by hand. Once a version is in winget-pkgs, the action maintains the package, and a manual edit
to the copy in this directory changes nothing upstream. This directory is the *seed*, not a mirror.
