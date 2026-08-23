# WireSockUI

[![Latest release](https://img.shields.io/github/v/release/wiresock/WireSockUI?display_name=release&sort=semver)](https://github.com/wiresock/WireSockUI/releases/latest)
[![CI](https://github.com/wiresock/WireSockUI/actions/workflows/ci.yml/badge.svg)](https://github.com/wiresock/WireSockUI/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**A minimal, lightweight Windows GUI for WireSock Secure Connect.**

WireSockUI is an additional desktop interface for the WireSock Secure Connect CLI/application-mode distribution. It provides a focused, file-based tunnel manager for users who prefer the straightforward feel of the classic WireGuard for Windows client.

WireSockUI is not a replacement for the full WireSock Secure Connect application. If you want a richer interface, dark theme, and more desktop conveniences, use the full [WireSock Secure Connect](https://www.wiresock.net/) experience.

## Screenshots

<p align="center">
  <img src="docs/images/wiresockui-main.png" alt="WireSockUI main window showing an active tunnel and connection statistics" width="900">
</p>

<table>
  <tr>
    <td width="50%"><img src="docs/images/wiresockui-settings.png" alt="WireSockUI settings dialog"></td>
    <td width="50%"><img src="docs/images/wiresockui-app-routing.png" alt="WireSockUI profile editor and application routing picker"></td>
  </tr>
  <tr>
    <td align="center"><sub>Startup, notification, adapter, Kill Switch, and logging options</sub></td>
    <td align="center"><sub>Profile editing and per-application routing</sub></td>
  </tr>
</table>

## What is WireSockUI?

WireSockUI is a compact WinForms front end for the direct WireSock SDK (`wgbooster.dll`) installed with WireSock Secure Connect CLI. It controls tunnels in-process rather than communicating with the full Secure Connect desktop application's service API.

It is intended for:

- users who manage WireGuard/WireSock `.conf` profiles and want a small native Windows interface;
- users who prefer a classic tunnel list with simple activate/deactivate controls;
- WireSock Secure Connect CLI users who want tray, startup, status, and log controls;
- x86/x64 systems that need the non-UWP build, including Windows 7 deployments.

## Features

- Create, import, edit, and manage WireGuard/WireSock profiles.
- Activate and deactivate tunnels and view transfer, latency, loss, interface, and peer state.
- Configure per-application routing from a searchable process picker.
- Use transparent or virtual network adapter mode and an optional Kill Switch.
- Run at Windows startup, minimize to the tray, and connect automatically.
- View, filter, clear, and follow SDK logs; change the runtime log level from Settings.
- Receive state-change notifications and update checks with the UWP-enabled build.
- Install architecture-matched x86, x64, and ARM64 packages.

## Configuration compatibility

WireSockUI supports **all WireSock configuration-file features available in the latest official release of WireSock Secure Connect**. The minimal interface does not reduce configuration-file functionality: WireSock and WireGuard directives are validated and passed to the matching installed direct SDK.

WireSock-specific options use the SDK's case-sensitive `#@ws:` extension syntax, for example:

```ini
#@ws:AllowedApps = app.exe
#@ws:DisallowedIPs = 192.168.1.0/24
#@ws:VirtualAdapterMode = false
```

Keep WireSockUI and the official WireSock Secure Connect CLI/SDK current when adopting newly released directives. Profiles containing `PreUp`, `PostUp`, `PreDown`, or `PostDown` script hooks are treated as privileged code and require confirmation before they are saved or activated.

## Windows and package variants

| Package | Intended platform | Difference |
| --- | --- | --- |
| `win-x64-uwp` / `win-x86-uwp` | **Windows 8.1 or later** | Includes notifications and update checks through Windows Runtime integrations. |
| `win-x64-no-uwp` / `win-x86-no-uwp` | **Windows 7 SP1 or Windows 8.1 and later** | Supports Windows 7 SP1 and omits Windows Runtime integrations. |
| `win-arm64-uwp` / `win-arm64-no-uwp` | Windows 11 on Arm | Native ARM64 builds; the UWP variant adds notifications and update checks. |

All variants use the same core tunnel and configuration functionality. Requirements:

- a matching-architecture WireSock Secure Connect CLI/SDK installation;
- administrator privileges;
- .NET Framework 4.7.2 or later on x86/x64, or .NET Framework 4.8.1 or later on ARM64;
- installation from the official MSI into its protected Program Files location.

Portable copies and loose publish directories are not supported.

Windows 8 is not supported because its final compatible runtime is .NET Framework 4.6.1, while WireSockUI targets .NET Framework 4.7.2.

## Installation

### 1. Install WireSock Secure Connect CLI and SDK

Open PowerShell or Windows Terminal and run:

```powershell
winget install --id NTKERNEL.WireSockVPNClientCLI --exact --source winget
```

This installs the WireSock driver, CLI components, and `wgbooster.dll` used by WireSockUI. If WinGet is unavailable, download the matching installer from the [official WireSock website](https://www.wiresock.net/).

### 2. Install WireSockUI

Download the MSI matching your Windows version and architecture from [GitHub Releases](https://github.com/wiresock/WireSockUI/releases). The installer can create Start-menu and desktop shortcuts; both options are selected by default.

> [!IMPORTANT]
> WireSockUI releases are intentionally unsigned. Windows displays **Unknown publisher** during installation. Verify a downloaded MSI against its published `.sha256` file before running it.

### 3. Add a tunnel

1. Start **WireSockUI** and accept the administrator prompt.
2. Select **Add Tunnel** to import a `.conf` file or create a profile.
3. Select the profile and choose **Activate**.

Do not run the WireSock CLI, service, or another direct-SDK tunnel at the same time; these clients share ownership of the WireSock driver session.

## Full WireSock Secure Connect GUI

WireSockUI deliberately keeps the interface small and conventional. For a modern, feature-rich desktop experience—including a dark theme and additional UI conveniences—install the full [WireSock Secure Connect](https://www.wiresock.net/) application instead.

Both choices use the WireSock platform; choose WireSockUI for a minimal file-oriented workflow and the full application for the complete desktop experience.

## Building from source

Visual Studio Build Tools with the C++ workload, a Windows SDK, and the repository-pinned .NET SDK are required. The example below builds and tests x64; replace `x64` with `x86` or `ARM64` as needed.

```powershell
dotnet restore WireSockUI.sln -p:Platform=x64 -m:1
$version = .\scripts\Resolve-BuildVersion.ps1
dotnet run --project WireSockUI.Tests\WireSockUI.Tests.csproj --configuration Release --framework net472-windows -p:Version=$version
dotnet build WireSockUI.sln --configuration Release -p:Platform=x64 -p:UseSharedCompilation=false -p:Version=$version -m:1
dotnet build WireSockUI.sln --configuration "Release UWP" -p:Platform=x64 -p:UseSharedCompilation=false -p:Version=$version -m:1
```

To build an unsigned MSI, first publish an architecture-specific payload, then run the installer builder:

```powershell
dotnet publish WireSockUI\WireSockUI.csproj --configuration Release --framework net472-windows --no-self-contained --no-restore -p:Platform=x64 -p:UseSharedCompilation=false -p:Version=$version -m:1
dotnet restore WireSockUI.Installer\WireSockUI.Installer.wixproj --locked-mode
.\scripts\Build-Msi.ps1 `
  -Platform x64 `
  -Version $version `
  -Flavor no-uwp `
  -PayloadDirectory .\bin\x64\Release\net472-windows\publish `
  -OutputDirectory .\artifacts\msi `
  -NoRestore
```

See [WireSockUI.Installer/README.md](WireSockUI.Installer/README.md) for installer validation and disposable-machine test guidance.

## Runtime and release notes

- Start the native `WireSockUI.exe`; `WireSockUI.Managed.dll` is not an application entry point.
- Profiles are normally stored under `%ProgramData%\WireSockUI\Configs` with administrator-only permissions. If that folder does not exist and the system ProgramData ACL is unsafe, WireSockUI uses an administrator-only data directory in the protected Program Files hierarchy instead of refusing to start. The fallback is shared across application architectures and remains selected on later launches; WireSockUI never copies data from an unsafe pre-existing ProgramData tree.
- Diagnostic logs are normally written to `%ProgramData%\WireSockUI\Logs\WireSockUI.log`; they follow the selected secure data root, remain bounded and rotated, and redact credentials and private keys.
- WireSockUI discovers the SDK from registered WireSock Secure Connect, Secure Connect Pro, and legacy CLI locations, then validates the architecture and protected installation path before loading it.
- Official releases contain six per-machine MSIs: x86, x64, and ARM64 in `uwp` and `no-uwp` variants. The MSI and its application modules are unsigned by policy; no signing certificate or signing environment variable is required.
- Each release includes SHA-256 sidecars, validation metadata, an SPDX SBOM, and GitHub artifact-provenance attestations.

## License and affiliation

WireSockUI is copyright &copy; 2023&ndash;2026 WireSock Foundation and distributed under the [MIT License](LICENSE).

The interface is intentionally similar in spirit to the simple, classic workflow of WireGuard for Windows. **WireSockUI is not affiliated with, endorsed by, or maintained by Jason A. Donenfeld or the WireGuard project.** References to WireGuard describe configuration compatibility and interface inspiration only; no official association is implied.
