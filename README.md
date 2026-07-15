# WireSockUI

WireSockUI is a lightweight WinForms interface for managing WireSock tunnels through the WireSock SDK `wgbooster.dll` API. It is intended for installations that provide the driver, the C++ CLI/service components, and `wgbooster.dll` directly.

WireSockUI does not talk to the newer WireSock Secure Connect service API. Keep using this project when you need the direct SDK/DLL integration model.

## Requirements

- Windows with a matching-architecture WireSock SDK installation.
- `wgbooster.dll` available next to `WireSockUI.exe` or installed through the WireSock SDK/minimal installer.
- The WireSock driver installed and usable by the current system.
- Administrator privileges. Starting with WireSock Secure Connect v3, the driver interface is available only to elevated users, so WireSockUI now always starts elevated.
- An administrator-owned installation directory. Before initializing settings or diagnostics, WireSockUI validates its executable, configuration, and every top-level DLL/EXE companion. Portable copies in user-writable locations are rejected because an elevated process must not load mutable application code.

At startup WireSockUI looks for `wgbooster.dll` in this order:

1. The application directory, unless that directory is writable by or owned by non-administrative users.
2. WireSock Secure Connect SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect`.
3. WireSock Secure Connect Pro SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect Pro`.
4. The legacy WireSock VPN Client registry location under `HKLM\SOFTWARE\NTKernelResources\WinpkFilterForVPNClient`.

For each registered install location it checks `sdk`, `bin`, and the install root. WireSockUI validates the directory, `wgbooster.dll`, and executable/DLL companion ownership and ACLs, then loads the exact validated DLL with a restricted DLL search path instead of changing the machine-wide environment or relying on `PATH`.

## Configuration Notes

WireSock-specific directives use the current SDK's exact, case-sensitive `#@ws:` comment-extension syntax:

```ini
#@ws:AllowedApps = app.exe
#@ws:DisallowedIPs = 192.168.1.0/24
#@ws:VirtualAdapterMode = false
#@ws:Socks5ProxyAllTraffic = false
```

Plain WireGuard keys are still parsed normally. WireSockUI validates current SDK fields such as script hooks, masking parameters, SOCKS5 settings, and profile-level `VirtualAdapterMode` while preserving the file-based profile workflow. Direct `wgbooster.dll` integration does not implement `BypassLanTraffic`; specify the LAN prefixes to bypass with `#@ws:DisallowedIPs` in `[Peer]` instead.

Amnezia 2.0 padding values `S1` through `S4` must be in the range `0..1279`. When any Amnezia padding/header option is present, `S1`, `S2`, and `H1` through `H4` are required; `S3` and `S4` remain optional. `H1` through `H4` accept fixed decimal values or inclusive decimal ranges and must not overlap after blank/zero values resolve to their WireGuard defaults. `Jmin` and `Jmax` must be specified together with `Jmin < Jmax`, and pre-handshake size/delay settings require either `Jc` or `Id`. Protocol imitation accepts the SDK's short and long protocol names, such as `quic`/`quic_initial` and `stun`/`stun_request`.

## Migration Notes

- Configuration section names, recognized key names, and `#@ws:` are validated with the same casing as the current SDK. Correct older lowercase or colon-less directives before activation.
- Save profiles as valid UTF-8 without a byte-order mark (BOM), matching the current SDK parser.
- `BypassLanTraffic`, `Table`, legacy `I1` through `I5`, and `Socks5Username` are rejected because the direct current `wgbooster.dll` parser does not apply them. Use `DisallowedIPs` and `Socks5ProxyUsername` where applicable.
- User-scoped settings are upgraded once after installing a new application version, preserving autorun, profile, adapter, notification, logging, and Kill Switch preferences.
- Profiles that remain writable by non-administrative users after startup hardening are not listed or activated. ACL hardening failures now stop initialization instead of continuing with a privileged, mutable configuration.

Profiles are stored in `%ProgramData%\WireSockUI\Configs` with an administrators-only ACL because WireSockUI runs elevated. Existing profiles from the older per-user `%AppData%\WireSockUI\Configs` folder are copied into `%ProgramData%\WireSockUI\PendingLegacyProfiles` and presented for explicit review in the full profile editor. The legacy source remains untouched until the reviewed profile is saved successfully; approval then removes the staged copy and original source. Name collisions are never overwritten automatically. Legacy migration and manual import use bounded temporary copies and reject reparse-point sources; profiles larger than 1 MiB must be moved or trimmed manually. Profile files that are reparse points are not loaded, imported, saved over, or activated; app-owned reparse-point files in the secured profile tree are removed during startup hardening, and unsafe directories or failed ACL updates stop startup. Script hooks are displayed and require confirmation before the reviewed profile can be saved or activated.

Runtime state that can be written by the elevated process, including the native recovery marker, is kept under the secured `%ProgramData%\WireSockUI` folder rather than per-user AppData. The UWP notification icon is stored under a dedicated `%ProgramData%\WireSockUI-Notifications` folder with a read-only Users ACL so the toast platform can load it without allowing unelevated writes.

Elevated autorun and SDK DLL loading are available only when the target file, containing directory, and replacement-sensitive ancestor path are administrator-owned. Install WireSockUI and the SDK into administrator-owned locations.

This release applies the same trust requirement to the complete WireSockUI application payload. Move existing portable installations to a directory created and owned by Administrators or `SYSTEM`, and ensure ordinary users have no write, delete, permission-change, or ownership rights on the executable, its `.config`, or companion DLL/EXE files.

Profiles containing `PreUp`, `PostUp`, `PreDown`, or `PostDown` script hooks require confirmation before import/save and again before activation. Treat script-hook profiles as privileged code.

The Settings dialog includes an optional Kill Switch toggle. When enabled, WireSockUI calls the `wgbooster.dll` network-lock API before creating the tunnel, preserves the native lock during reconnect/profile-switch cleanup, and clears the lock through normal tunnel cleanup when disconnecting. The option is off by default so existing SDK/minimal installations keep their current behavior.

If a native connect or cleanup call does not return, WireSockUI marks the state as indeterminate and disables further tunnel operations. It does not issue a concurrent reset against the process-global SDK while the original call is still executing. Once that call returns, WireSockUI performs sequence-checked cleanup and records a secured recovery marker if the outcome remains uncertain. Shutdown cleanup timeouts do not block process exit; the next elevated launch always queries the global network-lock state, including when no marker could be written. Use **Reset Kill Switch** from the tray menu while disconnected or in recovery mode if network access remains blocked.

Bounded diagnostic logs are written to `%ProgramData%\WireSockUI\Logs\WireSockUI.log`. The current log is limited to 1 MiB with three rotated archives, uses an administrators-only ACL, and redacts WireGuard private keys, preshared keys, SOCKS5 passwords, and URI credentials. Include these logs when reporting startup, recovery, or SDK-loading failures.

## Compatibility Notes

- The native `wgbooster.dll` ABI is expected to match the current SDK headers, including log levels, network-lock exports, and `drop_tunnel(..., preserve_network_lock)`.
- `Any CPU` builds disable 32-bit preference, and the solution includes x64 mappings for direct use with the common 64-bit SDK install.
- WireSockUI uses the same global direct-client event name as the C++ CLI/service to avoid running side by side with another direct SDK tunnel owner.
- The newer WireSock Secure Connect service stack is intentionally out of scope for this project.

## Building

```powershell
dotnet run --project WireSockUI.Tests\WireSockUI.Tests.csproj --configuration Release --framework net472-windows
dotnet build WireSockUI.sln --configuration Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1
dotnet build WireSockUI.sln --configuration "Release UWP" -p:Platform=x64 -p:UseSharedCompilation=false -m:1
```

The single-node `-m:1` solution build avoids a silent MSBuild failure that can happen when recent .NET SDKs schedule the WinForms app and the test project reference concurrently.

CI checks the direct-driver ABI against the pinned SDK contract snapshot under `sdk-contract`. The snapshot currently comes from `Wiresock-Foundation/wiresock-vpn-client` revision `aa72bc6ab8dce8f8128f74b8a6e3167b8caaf11a`; update the header, export definition, and `SDK_REVISION` together when intentionally adopting a newer SDK revision. A manual `SDK Integration` workflow runs an additional handle lifecycle smoke test on administrator-controlled, elevated self-hosted Windows runners labeled `wiresock-sdk`. Protect the `wiresock-sdk` GitHub environment with required reviewers, then configure `WIRESOCKUI_WGBOOSTER_PATH_X64` and `WIRESOCKUI_WGBOOSTER_PATH_ARM64` repository variables with trusted installed DLL paths; `WIRESOCKUI_TEST_PROFILE` is optional and enables a full start/query/stop/drop smoke test with a non-production profile.

## Releases

The release workflow signs `WireSockUI.exe`, verifies the Authenticode signature, generates an SPDX SBOM, publishes the ZIP with a SHA-256 checksum, and creates a GitHub artifact-provenance attestation. Configure these repository secrets before publishing a tag:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64`: base64-encoded PFX containing the code-signing certificate and private key.
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: password for that PFX.

Release tags must use the `vMAJOR.MINOR.PATCH` form. The workflow uses the repository `GITHUB_TOKEN`; a separate release PAT is not required.

## Remaining Runtime Risks

- The ABI contract job does not prove that the installed driver and SDK DLL work together. Keep the manual real-SDK smoke workflow available on representative x64 and ARM64 hosts.
- Tunnel start/stop still depends on driver state and Windows networking permissions after elevation succeeds.
- The `WireSockUI.Tests` harness covers parser/profile validation, native error-sentinel handling, lifecycle cleanup through a deterministic native facade, ACL checks, and reparse-point rejection. Real driver and `wgbooster.dll` validation remains environment-specific and is handled by the manual SDK Integration workflow.

## License

This project is licensed under the [MIT License](LICENSE).
