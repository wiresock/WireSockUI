# cronixWG

Windows VPN client: the existing WinForms UI plus an in-process `wireguard-go`
engine, optional VK TURN transport, and Mullvad application exclusions.
WireSock SDK, `wgbooster.dll`, `ndiswgc` and WinpkFilter are no longer runtime
requirements. Both ordinary WireGuard and VK profiles run in `vkturn-core.exe`.

This branch is a migration/test build. Windows driver/network behaviour still
requires testing on Windows; a successful Ubuntu cross-build is not that test.

## Windows test package

The Ubuntu build produces `out/cronixWG-windows-x64.zip` and
`out/windows-x64/`. Copy/extract the whole package to Windows 10/11 x64.
Use elevated 64-bit PowerShell in that folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
& "$env:ProgramFiles\cronixWG\WireSockUI.exe"
```

The installer checks payload hashes and publisher signatures, copies the files
into protected Program Files, and registers a demand-start driver service named
`cronixWGSplitTunnel`. It does not remove or modify another VPN's drivers. Existing
profiles remain in the original secure WireSockUI data directory.

.NET Framework 4.7.2+ is required. WebView2 Runtime enables the embedded VK captcha;
otherwise the existing browser captcha flow is used. Starting this elevated client
directly from an ordinary Downloads folder is not supported by its ACL checks.

See [the Windows test procedure](portable/WINDOWS-TESTING.txt) before reporting
network results. The UI reports Connected after a WireGuard handshake.
System routes and DNS switch only after the handshake, keeping VK captcha and
authentication reachable while the client is still connecting.

## Application exclusions

In a profile's `[Peer]` section:

```ini
#@ws:DisallowedApps = C:\Program Files\Mozilla Firefox\firefox.exe
```

Use full paths to existing local EXEs. The process picker now inserts full paths.
The supported policy is **VPN by AllowedIPs, except selected applications**.
Excluded sockets stay in the Windows network stack and use the internet interface;
there is no user-space TCP proxy or mini-NAT for bypass traffic.

The core registers process ancestry and executable device paths with the signed
Mullvad driver, supplies VPN/internet IP addresses, watches interface changes and
consumes driver events. Our driver is reset/stopped on normal disconnect. A borrowed driver is reset to
its original idle state and left running. Fatal
split-controller errors close the tunnel rather than silently ignoring exclusions.
The bundled version accepts private WFP sublayer GUIDs, so cronixWG does not reuse
Amnezia/Mullvad firewall sublayers. The global driver device name still prevents
simultaneous control by multiple VPN clients. Before starting its own service,
cronixWG attempts to reuse a running foreign driver only when its binary matches
the audited bundled version, exclusive access succeeds, and it is in STARTED
(unconfigured) state. Older Amnezia ABIs are not supported for reuse.
If reuse fails, the UI offers to stop the named split-driver service and retry
once. This requires an explicit Yes (No is the default); it does not kill other
VPN processes, stop dependent services, change startup settings, or uninstall
anything. A stopped foreign service is not restarted automatically; reconnect
or restart the other VPN when needed. If `AmneziaVPN-service` is running, the confirmation names both services and
stops the background service before the driver. If it starts later, a driver
stop timeout can offer a second confirmation. This may disconnect Amnezia.
Neither service is restarted automatically; start its background service or
reboot before returning to Amnezia if needed. A failure is shown in the UI.
The driver's provider/callout GUIDs are also shared, not just its device name.
STOPPED service status alone does not establish that those WFP objects have
been removed. Registered foreign callouts are never deleted automatically;
a duplicate-GUID failure after shutdown requires diagnosis and a reboot/retest.


`AllowedApps`, directory/name/wildcard application rules, `DisallowedIPs`, SOCKS
and script hooks are not implemented by this core. Unsupported routing directives
are rejected. The old WireSock Kill Switch must be disabled in Settings; this
migration does not provide an independent replacement. DNS is configured on the
VPN adapter, not isolated per application. IPv6 leak blocking is not implemented:
verify IPv6 separately when the server/profile has no IPv6 connectivity.

## Build on Ubuntu

Requirements: Go matching `core/go.mod`, Python 3, and .NET SDK 8 or newer.
The separate portable project builds .NET Framework against reference assemblies;
Visual Studio, Wine and a Windows SDK are not needed.

```bash
bash scripts/build-windows.sh
# Or select a privately installed Linux SDK:
DOTNET=/path/to/dotnet bash scripts/build-windows.sh
```

The script downloads hash-pinned, unmodified publisher binaries, runs Go tests,
cross-compiles the core for Windows amd64, compiles the existing C# UI, and creates
a folder/ZIP with install scripts and licensing files. Driver signatures are never
stripped. Network access is needed on the first build. The separate project uses
its own NuGet lock file. The old solution/native host remains for historical code
and regression coverage; the supported migration package is built by this script.

```bash
cd core
go test -race ./client ./server ./tcputil
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build ./client
```

## Structure

- `core/client/wg_windows.go`: Wintun, WireGuard lifecycle, routes/DNS, statistics.
- `core/client/wg_bridge.go`: `conn.Bind` to the existing TURN streams, no fork.
- `core/client/wg_direct.go`: direct UDP WireGuard for ordinary profiles.
- `core/client/split_windows.go`: signed Mullvad driver and private WFP sublayers.
- `core/client/split_config.go`: explicit 64-bit ABI serialization, tested on Linux.
- `WireSockUI/VkTurn/`: core process lifecycle and UI events for both transports.
- `portable/`: Linux-compatible UI project and Windows installation/test files.

## Provenance and licenses

The UI derives from WireSockUI (MIT; see [LICENSE](LICENSE)); upstream native
integration classes remain as legacy source but the migrated connection path does
not load them. VK TURN code retains [its original license](core/LICENSE).
WireGuard, Wintun and Mullvad are independent upstream projects, not affiliations.

The driver is Mullvad split tunnel 1.3.0.0, binary repository revision
`80c2e94d5ee8087ea80059ab24f1a81b632714f7`, source/ABI revision
`0a0eb97f67d1dbcb3d08bda66d3b24f465d95475`, distributed under MPL-2.0.
Wintun is version 0.14.1. Exact URLs and SHA-256 values are in
[scripts/fetch-windows-deps.py](scripts/fetch-windows-deps.py).
