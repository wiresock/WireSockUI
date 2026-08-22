# WireSock UI MSI

This standalone WiX 6 project packages an already-published, intentionally
unsigned, architecture-specific WireSock UI payload. It is intentionally not
part of `WireSockUI.sln`: the MSI must be built only after the native host has
embedded the final payload manifest. The builder rejects application modules
with embedded Authenticode certificate tables and verifies that the resulting
MSI is unsigned. The
per-machine MSI is the only supported distribution format;
portable ZIPs, loose publish directories, and direct copies into Program Files
are not supported installation or servicing mechanisms.

`WireSockUI.exe` is an in-process .NET Framework CLR host, not a bootstrapper
for a managed child process. Before starting the CLR it validates and locks the
runtime against an embedded canonical manifest containing the exact relative
path, length, and SHA-256 digest of every payload file. It then loads
`WireSockUI.Managed.dll` under `WireSockUI.exe.config`.

## Build

From the repository root:

```powershell
.\scripts\Build-Msi.ps1 `
  -Platform x64 `
  -Version 0.3.0 `
  -Flavor no-uwp `
  -PayloadDirectory .\artifacts\publish\win-x64 `
  -OutputDirectory .\artifacts\msi
```

Use `x86`, `x64`, or `ARM64`, and use flavor `uwp` or `no-uwp`. The version must
be a canonical three-field MSI version within `255.255.65535`. Starting with
`0.3.0`, unsigned input and output are mandatory; there is no signing override.

The command restores the exactly pinned `WixToolset.Sdk/6.0.2` and
`WixToolset.UI.wixext/6.0.2` packages unless `-NoRestore` is passed. It verifies
the native launcher's embedded payload
manifest, copies that exact allowlist into an isolated temporary staging
directory, builds the MSI, and validates its tables and cabinet contents. It
produces a deterministic MSI name and a persistent validation sidecar:

```text
WireSockUI-MAJOR.MINOR.PATCH-win-ARCH-FLAVOR.msi
WireSockUI-MAJOR.MINOR.PATCH-win-ARCH-FLAVOR.msi.validation.json
```

The ProductCode is also deterministically derived from version, architecture,
and flavor.
This makes a reinstall of the same version/architecture/flavor a maintenance or
repair operation on the same Windows Installer product. All architectures and
flavors share one UpgradeCode, so a same-version architecture or flavor change
is a major upgrade instead of a side-by-side install. Downgrades are blocked.
Same-version transition support must not be used to replace a published MSI
with different bytes; every published version is immutable.

`ComponentIdentityMap.json` is the reviewed servicing-history boundary for MSI
components. Never regenerate it wholesale. A resource keeps the same GUID for
its entire lifetime; when a resource is removed from every package, retain its
entry and change its state to `retired`. If that resource returns, reactivate
the existing entry and GUID. New resources require new `active` entries.
`Test-MsiArchitectureIsolation.ps1` compares every package against this map and
rejects GUID drift, unreviewed resources, reuse of retired identities, and
active identities omitted by the complete six-package release matrix.

## Security and servicing invariants

- Installation is per-machine (`ALLUSERS=1`) to a private, non-overridable
  Program Files property. The literal application-directory leaf is
  `WireSock Foundation WireSock UI`: x64 and ARM64 packages use
  `%ProgramFiles%\WireSock Foundation WireSock UI`, while x86 uses
  `%ProgramFiles(x86)%\WireSock Foundation WireSock UI`. This new, fixed
  single-leaf namespace intentionally never reuses or repairs the legacy
  `Program Files\WireSock UI` path: a user-controlled legacy directory could
  contain a junction or hard-linked file before elevation. The launcher path is
  stable across versions and flavors for a given architecture. An
  x86-to-x64/ARM64 migration changes the physical path and must disable and
  recreate path-bound per-user autorun state. The installer-owned notification
  AppUserModelID remains stable across architecture changes.
- No executable custom action runs. Standard MSI 5 `MsiLockPermissionsEx`
  authoring establishes a protected owner/DACL on the new application directory
  and propagates it to installed descendants. SYSTEM and Administrators receive
  full control; built-in Users receive read/execute only. The native host still
  validates the actual owner, DACL, link count, reparse state, hashes, and
  embedded payload manifest at every launch.
- Runtime profiles and diagnostics normally remain under the protected
  `%ProgramData%\WireSockUI` tree. If that application folder is absent and
  the ProgramData parent has an unsafe ACL, the elevated application creates
  an administrator-only data directory in the architecture-stable Program
  Files hierarchy. An existing unsafe `%ProgramData%\WireSockUI` tree remains
  a startup error and is never bypassed or repaired automatically. This fallback
  is deliberately outside the MSI payload so mutable data is not treated as an
  installed file and is retained across repair or uninstall. Once selected, it
  remains the active data root on later launches. WireSock UI never copies profiles or
  settings from an unsafe pre-existing ProgramData tree.
- The interactive installer exposes Start-menu and desktop shortcuts as
  independent optional features. Both are selected by default, including for
  unattended installs, and can be changed later through Windows Installer
  maintenance. They are stable, non-advertised all-users shortcuts at
  `Common Programs\WireSock UI.lnk` and `Public Desktop\WireSock UI.lnk`, both
  target the stable native launcher, and both are removed on uninstall. Major
  upgrades migrate the selected feature states. The Start-menu shortcut owns
  the stable AppUserModelID used by the UWP flavor for notifications. If that
  feature is deselected, notifications remain disabled and the application
  does not create a per-user replacement that could survive uninstall.
- Major upgrades remove the previous product inside the MSI transaction before
  installing the new product. Files removed from later releases are therefore
  removed as product-owned files; rollback restores the prior package if the
  new install fails. Unknown files are never recursively deleted.
- Same-version major upgrades are enabled only for an explicit architecture or
  flavor transition. The shared UpgradeCode prevents cross-architecture
  side-by-side products even when x86 and native 64-bit packages resolve to
  different Program Files roots.
- Reparse points are rejected from the source payload. Files are copied to fresh
  staging files, so source hard links or alternate file identities are not
  reproduced in the MSI.
- The launcher's embedded manifest is the runtime allowlist. Unknown
  source files fail packaging. PDBs, prior installers, archives, checksums,
  `_manifest` SBOM staging, SPDX JSON, and provenance JSONL are never staged.
- Every manifest-bound MSI file is either directly versioned or an unversioned
  companion of a directly versioned key file. The CLR consumes only
  `WireSockUI.exe.config`, so the identical library-named configuration copy is
  removed from publish output. This prevents ordinary MSI repair from
  preserving tampered product-owned files under the unversioned-file rules.
- The validation sidecar records the exact path, length, and SHA-256 digest of
  every runtime file. Pinned WiX 6 performs a non-executing cabinet extraction,
  which works even when the MSI target architecture differs from the validation
  runner. Validation confirms the extracted image matches that sidecar
  byte-for-byte, rechecks the launcher's architecture, proves every application
  EXE/DLL has no embedded Authenticode certificate table, and verifies the MSI
  itself reports Authenticode status `NotSigned`.
- .NET Framework 4.7.2 or later is a launch prerequisite for x86/x64. ARM64
  requires .NET Framework 4.8.1 because that release first added the native
  ARM64 CLR. The condition is bypassed only for maintenance of an
  already-installed product.

## Release integration

Release automation produces exactly six unsigned packages: x86, x64, and
ARM64, each in `no-uwp` and `uwp` flavors. Every application EXE/DLL inside the
cabinet must contain no embedded Authenticode certificate table, and the final
MSI must report Authenticode status `NotSigned`. A Windows installation may
still recognize an unchanged framework dependency through an external system
catalog; no such catalog or signature is embedded in the release.

Validate the unsigned result after building:

```powershell
.\scripts\Test-MsiPackage.ps1 `
  -MsiPath .\artifacts\msi\WireSockUI-0.3.0-win-x64-no-uwp.msi `
  -ValidationMetadataPath .\artifacts\msi\WireSockUI-0.3.0-win-x64-no-uwp.msi.validation.json `
  -ExpectedArchitecture x64 `
  -ExpectedVersion 0.3.0 `
  -ExpectedFlavor no-uwp
```

`Build-Msi.ps1` performs the same table, unsigned-artifact, and
extracted-cabinet validation before returning. The standalone validation
derives the ProductCode from version, architecture, and flavor and uses the
persistent sidecar to prove the cabinet payload is unchanged. The sidecar is
validation metadata, not a signature.

Each unsigned MSI is published with its `*.msi.validation.json`, a separate SPDX
SBOM generated from exactly the installed file set, and SHA-256 sidecars for all
three assets. GitHub artifact-provenance attestations cover the MSI, validation
document, and SBOM. These files remain external evidence and are never inserted
into the runtime cabinet. Publication rechecks all hashes and the authorized
tag, refuses to overwrite an existing GitHub release, and never uses asset
clobbering. Do not mutate or republish an MSI.

An elevated install smoke test is available for a disposable Windows VM:

```powershell
.\scripts\Test-MsiInstallation.ps1 `
  -MsiPath .\artifacts\msi\WireSockUI-0.3.0-win-x64-no-uwp.msi `
  -ValidationMetadataPath .\artifacts\msi\WireSockUI-0.3.0-win-x64-no-uwp.msi.validation.json `
  -EphemeralMachine
```

It creates a junction at the legacy application path pointing to a sentinel,
installs the package into the new namespace, proves the hostile legacy path and
sentinel were untouched, checks ownership/DACLs, file hashes, and shortcut
targeting, then tampers with every companion file plus a versioned runtime file
and the directory ACL and verifies ordinary and force-all MSI repair restore
them exactly. Finally, it uninstalls and verifies MSI-owned cleanup. The guard
is mandatory because the test changes Program Files and must run only on an
isolated machine with no existing WireSock UI installation or user data.

Hosted CI builds and statically validates all six MSI variants, checks
cross-architecture ProductCode/component isolation, runs the native host's
pre-CLR self-test, and runs the x64 MSI install/repair/uninstall scenario on a
guarded ephemeral runner. Release validation repeats the unsigned-cabinet
checks immediately before publication.

Verify all six release packages together so the validator can prove both
cross-architecture isolation and complete coverage of the reviewed component
identity map:

```powershell
.\scripts\Test-MsiArchitectureIsolation.ps1 `
  -MsiPath .\artifacts\msi\WireSockUI-0.3.0-win-x86-no-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-x64-no-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-arm64-no-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-x86-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-x64-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-arm64-uwp.msi

.\scripts\Test-MsiArchitectureIsolation.Tests.ps1 `
  -MsiPath .\artifacts\msi\WireSockUI-0.3.0-win-x86-no-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-x64-no-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-arm64-no-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-x86-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-x64-uwp.msi,`
           .\artifacts\msi\WireSockUI-0.3.0-win-arm64-uwp.msi
```

Close WireSock UI before maintenance. Windows Restart Manager handles normal
interactive file-in-use cases, while unattended deployment must treat MSI exit
codes `1603` and `3010` according to the deployment system's restart policy.
Before uninstalling or changing from x86 to x64/ARM64, disable WireSock UI
autorun for each affected account. MSI intentionally does not enumerate or
delete other users' scheduled tasks or notification shortcuts; that per-user
cleanup belongs to the verified application lifecycle.

Uninstall removes MSI-owned runtime files, installer registry state, and the
all-users Start-menu and desktop shortcuts. It does not recursively delete
unknown files and does not remove application-created profiles, protected
preferences, recovery state, or logs under `%ProgramData%` or the protected
Program Files fallback directories. It also does not remove another user's
Task Scheduler autorun definition. Settings from the former managed-EXE
`LocalFileSettingsProvider` identity are migrated separately by the application
through a bounded, allowlisted reader; autorun is never migrated from
`user.config`.

WiX 6.0.2 remains in its consumer security-fix window through February 5, 2027.
Organizations deriving revenue from WiX releases must evaluate and satisfy the
WiX Open Source Maintenance Fee terms before using the tool in production.
