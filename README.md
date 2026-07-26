# WireSockUI

WireSockUI is a lightweight WinForms interface for managing WireSock tunnels through the WireSock SDK `wgbooster.dll` API. It is intended for installations that provide the driver, the C++ CLI/service components, and `wgbooster.dll` directly.

WireSockUI does not talk to the newer WireSock Secure Connect service API. Keep using this project when you need the direct SDK/DLL integration model.

## Requirements

- Windows with a matching-architecture WireSock SDK installation.
- `wgbooster.dll` available next to `WireSockUI.exe` or installed through the WireSock SDK/minimal installer.
- The WireSock driver installed and usable by the current system.
- Administrator privileges. Starting with WireSock Secure Connect v3, the driver interface is available only to elevated users, so WireSockUI now always starts elevated. The elevated token must belong to the account signed in to the current desktop session; over-the-shoulder UAC credentials from a different administrator are rejected so per-user autorun and notification artifacts cannot be redirected to that account.
- An administrator-owned installation directory. Before initializing settings or diagnostics, WireSockUI recursively validates its executable, configuration, payload directories, and every DLL/EXE companion without following reparse points. Portable copies in user-writable locations are rejected because an elevated process must not load mutable application code. Application payload validation stops at 4,096 entries so a malformed installation cannot make elevated startup consume unbounded resources.

At startup WireSockUI looks for `wgbooster.dll` in this order:

1. The application directory, unless that directory is writable by or owned by non-administrative users.
2. WireSock Secure Connect SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect`.
3. WireSock Secure Connect Pro SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect Pro`.
4. The legacy WireSock VPN Client registry location under `HKLM\SOFTWARE\NTKernelResources\WinpkFilterForVPNClient`.

For each registered install location it checks `sdk`, `bin`, and the install root. WireSockUI validates the directory, `wgbooster.dll`, and executable/DLL companion ownership and ACLs, then loads the exact validated DLL with a restricted DLL search path instead of changing the machine-wide environment or relying on `PATH`. SDK companion validation is limited to 1,024 entries per candidate directory.

## Configuration Notes

WireSock-specific directives use the current SDK's exact, case-sensitive `#@ws:` comment-extension syntax:

```ini
#@ws:AllowedApps = app.exe
#@ws:DisallowedIPs = 192.168.1.0/24
#@ws:VirtualAdapterMode = false
#@ws:Socks5ProxyAllTraffic = false
```

Plain WireGuard keys are still parsed normally. WireSockUI validates current SDK fields such as script hooks, masking parameters, SOCKS5 settings, and profile-level `VirtualAdapterMode` while preserving the file-based profile workflow. Direct `wgbooster.dll` integration does not implement `BypassLanTraffic`; specify the LAN prefixes to bypass with `#@ws:DisallowedIPs` in `[Peer]` instead.

The elevated native engine receives only the exact, allowlisted `[Interface]` and `[Peer]` directives understood by this WireSockUI/SDK contract. Unknown sections and keys—including future script-like directives—are rejected; upgrade WireSockUI and the SDK together when adopting a new native directive.

Amnezia 2.0 padding values `S1` through `S4` must be in the range `0..1279`. When any Amnezia padding/header option is present, `S1`, `S2`, and `H1` through `H4` are required; `S3` and `S4` remain optional. `H1` through `H4` accept fixed decimal values or inclusive decimal ranges and must not overlap after blank/zero values resolve to their WireGuard defaults. `Jmin` and `Jmax` must be specified together with `Jmin < Jmax`, and pre-handshake size/delay settings require either `Jc` or `Id`. Protocol imitation accepts the SDK's short and long protocol names, such as `quic`/`quic_initial` and `stun`/`stun_request`.

## Migration Notes

- Configuration section names, recognized key names, and `#@ws:` are validated with the same casing as the current SDK. Correct older lowercase or colon-less directives before activation.
- Unknown sections and directives are rejected instead of being passed through to the elevated native parser. Update WireSockUI and the WireSock SDK together before using a newly introduced configuration key.
- Save profiles as valid UTF-8. A leading UTF-8 byte-order mark (BOM) is accepted by the current SDK and WireSock UI; UTF-16 and UTF-32 profiles remain unsupported.
- Empty comma-separated items in `Address`, `AllowedIPs`, and `DisallowedIPs` are ignored to match the current SDK route parser. Required `Address` and `AllowedIPs` lists must still contain at least one usable entry, and non-empty malformed routes are rejected. Empty DNS list items remain invalid so the UI does not silently accept an unusable resolver configuration.
- `BypassLanTraffic`, `Table`, legacy `I1` through `I5`, and `Socks5Username` are rejected because the direct current `wgbooster.dll` parser does not apply them. Use `DisallowedIPs` and `Socks5ProxyUsername` where applicable.
- User-scoped cosmetic settings are upgraded once after installing a new application version. Auto-connect, last profile, adapter mode, and Kill Switch preferences are stored in administrator-protected `%ProgramData%\WireSockUI\PrivilegedSettings.xml`; the first version using this store displays legacy values and requires explicit confirmation before importing them from user-writable settings.
- Profiles that remain writable by non-administrative users after startup hardening are not listed or activated. ACL hardening failures now stop initialization instead of continuing with a privileged, mutable configuration.

Profiles are stored in `%ProgramData%\WireSockUI\Configs` with an administrators-only ACL because WireSockUI runs elevated. Existing profiles from the older per-user `%AppData%\WireSockUI\Configs` folder are copied into `%ProgramData%\WireSockUI\PendingLegacyProfiles` and presented for explicit review in the full profile editor. The legacy source remains untouched until the reviewed profile is saved successfully; approval then removes the staged copy and original source. Name collisions are never overwritten automatically, including names that differ only by letter case on case-sensitive Windows directories. Legacy migration, manual import, catalog loading, activation, and editing enforce a 1 MiB profile limit and reject reparse-point sources; trim larger profiles before using them. The active, legacy, and pending profile catalogs are each limited to 1,024 directory entries, including non-profile files and subdirectories, so startup cannot be stalled by an unbounded directory; archive unused entries before exceeding that limit. Startup ACL hardening also stops when a secured data-tree traversal exceeds 4,096 entries. Profile files that are reparse points are not loaded, imported, saved over, or activated; app-owned reparse-point files in the secured profile tree are removed during startup hardening, and unsafe directories or failed ACL updates stop startup. Script hooks are displayed and require confirmation before the reviewed profile can be saved or activated.

Profile edits and imports are staged in the administrator-owned `%ProgramData%\WireSockUI\Configs\.transactions` directory. Rename intent is journaled with write-through file operations before the visible profile name changes. The next startup completes or safely abandons an interrupted rename and removes app-owned temporary files, including temporary files left by older WireSockUI versions. Do not place user-managed files in `.transactions`.

Runtime state that can be written by the elevated process, including protected connection settings and the native recovery marker, is kept under the secured `%ProgramData%\WireSockUI` folder rather than per-user AppData. The UWP notification icon is stored under a dedicated `%ProgramData%\WireSockUI-Notifications` folder with a read-only Users ACL so the toast platform can load it without allowing unelevated writes.

Elevated autorun and SDK DLL loading are available only when the target file, containing directory, and replacement-sensitive ancestor path are administrator-owned. Install WireSockUI and the SDK into administrator-owned locations.

Driver ownership remains coordinated with the SDK CLI/service through the shared `Global\WiresockClientService` object. WireSock UI validates that object's owner and access rules and fails closed when it was pre-created with an incompatible or untrusted security descriptor; close the process that owns the conflicting object before retrying. Because this fixed global name is an SDK compatibility primitive and Windows cannot replace a kernel object while another process holds it, an ordinary process can squat on the name and deny WireSock UI startup. The squatting object is never trusted and cannot grant driver control, but eliminating this availability risk requires a coordinated SDK/service bootstrap or protected-private-namespace change.

Autorun tasks are scoped to the current Windows user and have no execution-time limit. Opening and saving Settings migrates a validated older WireSockUI autorun task to the current definition. A lone regular file at the legacy per-user Startup shortcut name is not trusted as proof that autorun was enabled and defaults to off. WireSockUI never deletes that opaque artifact or replaces it with a protected elevated task without explicit cleanup or migration confirmation; deletion is deferred until the rest of the Settings transaction commits. Settings are applied as a compensating transaction: autorun, runtime log level, Kill Switch state, and persisted preferences are rolled back together when a later step fails. When compensation verifies the native state, WireSockUI clears the temporary recovery latch and restores the previous connection state and monitor instead of requiring an application restart.

This release applies the same trust requirement to the complete WireSockUI application payload. Move existing portable installations to a directory created and owned by Administrators or `SYSTEM`, and ensure ordinary users have no write, delete, permission-change, or ownership rights on the executable, its `.config`, or companion DLL/EXE files.

Profiles containing `PreUp`, `PostUp`, `PreDown`, or `PostDown` script hooks require confirmation before import/save and again before activation. Treat script-hook profiles as privileged code.

Script-hook confirmation displays every complete command in a scrollable, read-only view, escapes invisible control and bidirectional-formatting characters, and defaults to rejection. Profile names are limited to a single Windows filesystem component of at most 250 characters.

The Settings dialog includes an optional Kill Switch toggle. When enabled, WireSockUI calls the `wgbooster.dll` network-lock API before creating the tunnel, preserves the native lock during reconnect/profile-switch cleanup, and clears the lock through normal tunnel cleanup when disconnecting. The option is off by default so existing SDK/minimal installations keep their current behavior.

If a native connect or cleanup call does not return, WireSockUI marks the state as indeterminate and disables further tunnel operations. It does not issue a concurrent reset against the process-global SDK while the original call is still executing. Once that call returns, WireSockUI performs sequence-checked cleanup and records a secured recovery marker if the outcome remains uncertain. Recovery markers are replaced atomically and carry operation ownership, so an older late completion cannot remove or overwrite a newer failure record. A successful compensation or preserved-lock retry clears the corresponding recovery state immediately. Shutdown cleanup timeouts do not block process exit; the next elevated launch always queries the global network-lock state, including when no marker could be written. Use **Reset Kill Switch** from the tray menu while disconnected or in recovery mode if network access remains blocked.

Bounded diagnostic logs are written to `%ProgramData%\WireSockUI\Logs\WireSockUI.log`. The current log is limited to 1 MiB with three rotated archives, uses an administrators-only ACL, and redacts WireGuard private keys, preshared keys, SOCKS5 passwords, and URI credentials. Native UI logs are also bounded and drained in batches; retained native records are limited to 4,096 UTF-16 code units and carry a `[truncated]` suffix when shortened. The log view reports how many entries were dropped when either queue is saturated. Include these logs when reporting startup, recovery, or SDK-loading failures.

## Compatibility Notes

- The native `wgbooster.dll` ABI is expected to match the current SDK headers, including log levels, network-lock exports, and `drop_tunnel(..., preserve_network_lock)`.
- `Any CPU` builds disable 32-bit preference. The solution and release matrix include explicit x86, x64, and ARM64 mappings; hosted managed tests execute under x86 and x64, while real driver smoke tests run on the available x64 and ARM64 SDK hosts.
- WireSockUI uses the same global direct-client event name as the C++ CLI/service to avoid running side by side with another direct SDK tunnel owner.
- The newer WireSock Secure Connect service stack is intentionally out of scope for this project.

## Building

```powershell
dotnet restore WireSockUI.sln -p:Platform=x64 -m:1
dotnet run --project WireSockUI.Tests\WireSockUI.Tests.csproj --configuration Release --framework net472-windows
dotnet build WireSockUI.sln --configuration Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1
dotnet build WireSockUI.sln --configuration "Release UWP" -p:Platform=x64 -p:UseSharedCompilation=false -m:1
```

Use `-- --list-tests` to list test names or `-- --filter "profile catalog"` to run a focused subset with full exception diagnostics. Each executed test has a two-minute timeout, and CI jobs have explicit overall timeouts so a deadlock reports the active test instead of occupying a runner indefinitely.

The single-node `-m:1` solution build avoids a silent MSBuild failure that can happen when recent .NET SDKs schedule the WinForms app and the test project reference concurrently.

CI checks both the native header/export ABI and the managed P/Invoke declarations against the pinned SDK contract snapshot under `sdk-contract`. The snapshot currently comes from `Wiresock-Foundation/wiresock-vpn-client` revision `a5183451c62b42abe0a5fb67be215bb5a9375603`; update the header, export definition, and `SDK_REVISION` together when intentionally adopting a newer SDK revision. A scheduled or manually dispatched drift workflow compares the snapshot with the current private upstream repository using the `MY_GITHUB_PAT` Actions secret, which must be scoped to read-only repository contents and is never exposed to pull-request jobs. The .NET SDK is pinned by `global.json`, NuGet lock files are committed, and CI restores in locked mode.

Pull requests run only hosted contract and managed tests. They never execute branch code on the elevated SDK hosts. Real SDK integration runs after protected `main` updates, weekly, manually, and before releases. The integration workflow exercises transparent and virtual-adapter lifecycle, network-lock enable/reset behavior, and a complete Amnezia 2.0 profile. Protect the `wiresock-sdk` environment with required reviewers and use disposable, just-in-time x64 and ARM64 self-hosted runners labeled `wiresock-sdk`; do not reuse a runner after a job. Runners must use version `2.329.0` or newer for the Node.js 24 actions and checkout credential handling. Set `WIRESOCKUI_SDK_INTEGRATION_ENABLED=true` only after both disposable runner pools are available. Protected `main` checks warn and skip real-SDK work when validation is unavailable; release checks remain fail-closed, while scheduled and manual runs remain optional.

Configure `WIRESOCKUI_WGBOOSTER_PATH_X64` and `WIRESOCKUI_WGBOOSTER_PATH_ARM64` with trusted installed DLL paths. Configure `WIRESOCKUI_TEST_PROFILE_TRANSPARENT`, `WIRESOCKUI_TEST_PROFILE_VIRTUAL_ADAPTER`, and `WIRESOCKUI_TEST_PROFILE_AMNEZIA` with dedicated, administrator-owned, non-production profiles without script hooks. The Amnezia profile must contain `S1`-`S4`, `H1`-`H4`, `Id`, `Ip`, and `Ib`; the legacy `WIRESOCKUI_TEST_PROFILE` remains a fallback for the two standard modes. The workflow fails when a required profile is missing or mutable by non-administrative users.

Native state and statistics polling use bounded asynchronous queries. If `wgbooster.dll` does not return before the query timeout, WireSockUI stops issuing additional native operations, records a recovery marker, and requires recovery or restart. Startup also compares the process and `wgbooster.dll` PE architectures so x86/x64/ARM64 mismatches are reported directly.

## Releases

The release workflow signs `WireSockUI.exe`, verifies the Authenticode signature, generates an SPDX SBOM, publishes the ZIP with a SHA-256 checksum, and creates a GitHub artifact-provenance attestation. Configure these repository secrets before publishing a tag:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64`: base64-encoded PFX containing the code-signing certificate and private key.
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: password for that PFX.

Release tags must use the `vMAJOR.MINOR.PATCH` form. The workflow uses the repository `GITHUB_TOKEN`; a separate release PAT is not required.

## Remaining Runtime Risks

- The ABI contract job does not prove that the installed driver and SDK DLL work together. Keep the release-gated real-SDK smoke runners available on representative x64 and ARM64 hosts.
- Tunnel start/stop still depends on driver state and Windows networking permissions after elevation succeeds.
- The global `WiresockClientService` event is an SDK compatibility primitive shared with the direct C++ CLI. WireSockUI rejects unexpected owners and broad ACLs, but changing to a private authenticated namespace requires a coordinated SDK change.
- The `WireSockUI.Tests` harness covers parser/profile validation, native error-sentinel handling, lifecycle cleanup and bounded monitoring through a deterministic native facade, ACL checks, architecture matching, transactional profile renames, and reparse-point rejection. Real driver and `wgbooster.dll` validation remains environment-specific and is handled by the SDK Integration workflow.

## License

This project is licensed under the [MIT License](LICENSE).
