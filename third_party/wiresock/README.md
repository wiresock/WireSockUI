# WireSock binaries (local only)

cronixWG uses two parts of the WireSock SDK directly instead of the WireSock CLI.
They come from **WireSock Secure Connect CLI 3.8.2** (`NTKERNEL.WireSockVPNClientCLI`):
the SDK `wgbooster.dll` 3.8.2.1 matches `sdk-contract/wgbooster/wgbooster.def`
(36 exports), the filter driver is 3.7.1.1.

| File | Role |
|---|---|
| `sdk/wgbooster.dll` (copied to `../../wgbooster.dll` for the build) | WireGuard engine, profile parser, per-app split tunnelling, Kill Switch (network lock). Loaded by the UI from its own folder. |
| `sdk/wgbooster.h`, `sdk/wgbooster.md` | SDK header and documentation, for reference. |
| `drivers/win10`, `drivers/win8`, `drivers/win7` (`ndiswgc_lwf.inf/.sys/.cat`) | NDIS lightweight filter (device `\\.\NDISWGC`) that intercepts packets for `wgbooster.dll`. Required. |
| `wiresock-adapter/win10`, `wiresock-adapter/legacy` + `devcon.exe` | Virtual network adapter, only for `VirtualAdapterMode = true`. Optional. |
| `certificates/rootac.cer` | Root certificate needed to install the drivers on Windows 7/8.x. |

These files are **not committed** (see `.gitignore`).

## Getting them without installing the CLI

The CLI installer is a WiX Burn bundle with two MSIs (WireSockKernel, WireSockSdk)
in its attached container:

1. `winget download --id NTKERNEL.WireSockVPNClientCLI --architecture x64 -d <dir>`
   (winget verifies the installer hash).
2. Cut the attached cab out of the .exe (the `MSCF` block after the UX container)
   and extract it with 7-Zip: you get `a0` (kernel MSI) and `a1` (SDK MSI).
3. `msiexec /a a0.msi /qn TARGETDIR=<dir>` and the same for `a1.msi`; the files land in
   `<dir>\PFiles64\WireSock Secure Connect\`. Nothing is installed.

Keep `.sys`, `.inf` and `.cat` byte-identical: the driver signature lives in the `.cat`.
`wgbooster.dll` and `ndiswgc.sys` must come from the same release.

## Drivers

```powershell
.\scripts\WireSockDriver.ps1                          # status
.\scripts\WireSockDriver.ps1 -Action Install          # filter only
.\scripts\WireSockDriver.ps1 -Action Install -VirtualAdapter
.\scripts\WireSockDriver.ps1 -Action Uninstall        # removes only what cronixWG installed
```
