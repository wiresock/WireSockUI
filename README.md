# WireSockUI

WireSockUI is a lightweight WinForms interface for managing WireSock tunnels through the WireSock SDK `wgbooster.dll` API. It is intended for installations that provide the driver, the C++ CLI/service components, and `wgbooster.dll` directly.

WireSockUI does not talk to the newer WireSock Secure Connect service API. Keep using this project when you need the direct SDK/DLL integration model.

## Requirements

- Windows with a matching-architecture WireSock SDK installation.
- `wgbooster.dll` available next to `WireSockUI.exe` or installed through the WireSock SDK/minimal installer.
- The WireSock driver installed and usable by the current system.
- Administrator privileges when the selected tunnel mode or driver operations require elevation.

At startup WireSockUI looks for `wgbooster.dll` in this order:

1. The application directory.
2. WireSock Secure Connect SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect`.
3. WireSock Secure Connect Pro SDK registry install locations under `HKLM\Software\WireSock Foundation\WireSock Secure Connect Pro`.
4. The legacy WireSock VPN Client registry location under `HKLM\SOFTWARE\NTKernelResources\WinpkFilterForVPNClient`.
5. Directories already present on the process `PATH` as a fallback.

For each registered install location it checks `sdk`, `bin`, and the install root. The discovered directory is added to the process `PATH` so the native library can be loaded without changing the machine-wide environment.

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

## Compatibility Notes

- The native `wgbooster.dll` ABI is expected to match the current SDK headers, including log levels and `drop_tunnel(..., preserve_network_lock)`.
- `Any CPU` builds disable 32-bit preference, and the solution includes x64 mappings for direct use with the common 64-bit SDK install.
- WireSockUI uses the same global direct-client event name as the C++ CLI/service to avoid running side by side with another direct SDK tunnel owner.
- The newer WireSock Secure Connect service stack is intentionally out of scope for this project.

## Building

```powershell
dotnet build WireSockUI.sln
dotnet build WireSockUI.sln -p:Platform=x64
```

## Remaining Runtime Risks

- A clean build does not prove that the installed driver and SDK DLL are present or compatible on the target machine.
- Tunnel start/stop still depends on driver state, Windows networking permissions, and elevation.
- There is no automated test project in this repository yet; compatibility is currently guarded by build checks and focused validation logic.

## License

This project is licensed under the [MIT License](LICENSE).
