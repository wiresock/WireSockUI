# WireSockUI

WireSockUI is a lightweight WinForms interface for managing WireSock tunnels through the WireSock SDK `wgbooster.dll` API. It is intended for installations that provide the driver, the C++ CLI/service components, and `wgbooster.dll` directly.

WireSockUI does not talk to the newer WireSock Secure Connect service API. Keep using this project when you need the direct SDK/DLL integration model.

## Requirements

- Windows with a matching-architecture WireSock SDK installation.
- `wgbooster.dll` available next to `WireSockUI.exe` or installed through the WireSock SDK/minimal installer.
- The WireSock driver installed and usable by the current system.
- Administrator privileges. Starting with WireSock Secure Connect v3, the driver interface is available only to elevated users, so WireSockUI now always starts elevated.

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

Profiles are stored in `%ProgramData%\WireSockUI\Configs` with an administrators-only ACL because WireSockUI runs elevated. Existing profiles from the older per-user `%AppData%\WireSockUI\Configs` folder are moved into the secured folder on startup when no secured profile with the same name exists. Legacy migration and manual import use bounded temporary copies and reject reparse-point sources; profiles larger than 1 MiB must be moved or trimmed manually. If a secured profile already exists, the legacy copy is deleted only when its content matches. Profile files that are reparse points are not loaded, imported, saved over, or activated; app-owned reparse-point files in the secured profile tree are removed during startup hardening, and unsafe directories or failed ACL updates stop startup. Legacy profiles containing script hooks are not auto-migrated; import them manually so the hook warning can be reviewed.

Runtime state that can be written by the elevated process, including the native recovery marker, is kept under the secured `%ProgramData%\WireSockUI` folder rather than per-user AppData. The UWP notification icon is stored under a dedicated `%ProgramData%\WireSockUI-Notifications` folder with a read-only Users ACL so the toast platform can load it without allowing unelevated writes.

Elevated autorun and SDK DLL loading are available only when the target file, containing directory, and replacement-sensitive ancestor path are administrator-owned. Install WireSockUI and the SDK into administrator-owned locations.

Profiles containing `PreUp`, `PostUp`, `PreDown`, or `PostDown` script hooks require confirmation before import/save and again before activation. Treat script-hook profiles as privileged code.

The Settings dialog includes an optional Kill Switch toggle. When enabled, WireSockUI calls the `wgbooster.dll` network-lock API before creating the tunnel, preserves the native lock during reconnect/profile-switch cleanup, and clears the lock through normal tunnel cleanup when disconnecting. The option is off by default so existing SDK/minimal installations keep their current behavior.

If a native connect cleanup call does not return, WireSockUI attempts to reset the network lock and disables further tunnel operations with a recovery warning instead of leaving the Activate button silently stuck. Shutdown cleanup timeouts do not block process exit; if WireSockUI cannot verify/reset the network lock during shutdown it writes a secured recovery marker and shows a visible startup warning on the next elevated launch. Restart WireSockUI as administrator and use **Reset Kill Switch** from the tray menu while disconnected or in recovery mode if network access remains blocked.

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

## Remaining Runtime Risks

- A clean build does not prove that the installed driver and SDK DLL are present or compatible on the target machine.
- Tunnel start/stop still depends on driver state and Windows networking permissions after elevation succeeds.
- The `WireSockUI.Tests` harness covers parser/profile validation, native error-sentinel handling, lifecycle cleanup through a deterministic native facade, ACL checks, and reparse-point rejection. Actual driver and `wgbooster.dll` operation still needs runtime validation on a machine with the SDK installed.

## License

This project is licensed under the [MIT License](LICENSE).
