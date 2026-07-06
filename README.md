# WireSockUI

WireSockUI is a lightweight WinForms interface for managing WireSock tunnels through the WireSock SDK `wgbooster.dll` API. It is intended for installations that provide the driver, the C++ CLI/service components, and `wgbooster.dll` directly.

WireSockUI does not talk to the newer WireSock Secure Connect service API. Keep using this project when you need the direct SDK/DLL integration model.

## Requirements

- Windows with a matching-architecture WireSock SDK installation.
- `wgbooster.dll` available next to `WireSockUI.exe` or installed through the WireSock SDK/minimal installer.
- The WireSock driver installed and usable by the current system.
- Administrator privileges. Starting with WireSock Secure Connect v3, the driver interface is available only to elevated users, so WireSockUI now always starts elevated.

At startup WireSockUI looks for `wgbooster.dll` in this order:

1. The application directory, unless that directory is writable by non-administrative users.
2. WireSock Secure Connect SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect`.
3. WireSock Secure Connect Pro SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect Pro`.
4. The legacy WireSock VPN Client registry location under `HKLM\SOFTWARE\NTKernelResources\WinpkFilterForVPNClient`.

For each registered install location it checks `sdk`, `bin`, and the install root. The discovered directory is registered with the process through `SetDllDirectory` so the native library can be loaded without changing the machine-wide environment.

## Configuration Notes

WireSock-specific directives may use the current SDK comment-extension syntax:

```ini
#@ws:AllowedApps = app.exe
#@ws:DisallowedIPs = 192.168.1.0/24
#@ws:VirtualAdapterMode = false
#@ws:BypassLanTraffic = false
#@ws:Socks5ProxyAllTraffic = false
```

Plain WireGuard keys are still parsed normally. WireSockUI validates common SDK fields such as script hooks, masking parameters, SOCKS5 settings, `BypassLanTraffic`, and profile-level `VirtualAdapterMode` while preserving the file-based profile workflow.

Profiles are stored in `%ProgramData%\WireSockUI\Configs` with an administrators-only ACL because WireSockUI runs elevated. Existing profiles from the older per-user `%AppData%\WireSockUI\Configs` folder are moved into the secured folder on startup when no secured profile with the same name exists. Legacy migration and manual import use bounded temporary copies and reject reparse-point sources; profiles larger than 1 MiB must be moved or trimmed manually. If a secured profile already exists, the legacy copy is deleted only when its content matches. Profile files that are reparse points are not loaded, imported, saved over, or activated; app-owned reparse-point files in the secured profile tree are removed during startup hardening when possible. Legacy profiles containing script hooks are not auto-migrated; import them manually so the hook warning can be reviewed.

Profiles containing `PreUp`, `PostUp`, `PreDown`, or `PostDown` script hooks require confirmation before import/save and again before activation. Treat script-hook profiles as privileged code.

The Settings dialog includes an optional Kill Switch toggle. When enabled, WireSockUI calls the `wgbooster.dll` network-lock API before creating the tunnel, preserves the native lock during reconnect/profile-switch cleanup, and clears the lock through normal tunnel cleanup when disconnecting. The option is off by default so existing SDK/minimal installations keep their current behavior.

If a native connect cleanup call does not return, WireSockUI attempts to reset the network lock and disables further tunnel operations with a recovery warning instead of leaving the Activate button silently stuck. Shutdown cleanup timeouts do not block process exit; if WireSockUI cannot verify/reset the network lock during shutdown it writes a secured recovery marker and shows a visible startup warning on the next elevated launch. Restart WireSockUI as administrator and use **Reset Kill Switch** from the tray menu if network access remains blocked.

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
- The `WireSockUI.Tests` harness covers focused parser/profile validation scenarios, but native driver and `wgbooster.dll` lifecycle behavior still needs runtime validation on a machine with the SDK installed.

## License

This project is licensed under the [MIT License](LICENSE).
