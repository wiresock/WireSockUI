# WireSock binaries (local only)

cronixWG uses two parts of WireSock directly instead of the WireSock CLI:

| File | Role |
|---|---|
| `../../wgbooster.dll` | WireGuard engine, profile parser and per-app split tunnelling. Loaded by the UI from its own folder. |
| `drivers/ndiswgc_lwf.inf`, `.sys`, `.cat` (`win10/` for Windows 10/11) | NDIS lightweight filter (device `\\.\NDISWGC`) that intercepts packets for `wgbooster.dll`. Required. |
| `wiresock-adapter/` (`wiresock.inf/.sys/.cat`, `devcon.exe`) | Virtual network adapter, only for `VirtualAdapterMode = true`. Optional. |
| `certificates/rootac.cer` | Root certificate needed to install the drivers on Windows 7/8.x. |

These files are **not committed** (see `.gitignore`). Copy them from a WireSock
Secure Connect installation (`C:\Program Files\WireSock Secure Connect\`), keeping
`.sys`, `.inf` and `.cat` byte-identical: the driver signature lives in the `.cat`.

`wgbooster.dll` and `ndiswgc.sys` must come from the **same WireSock release**
(currently driver 3.7.1.1 from WireSock Secure Connect).

Install / remove / check the drivers (elevated PowerShell for Install and Uninstall):

```powershell
.\scripts\WireSockDriver.ps1                          # status
.\scripts\WireSockDriver.ps1 -Action Install          # filter only
.\scripts\WireSockDriver.ps1 -Action Install -VirtualAdapter
.\scripts\WireSockDriver.ps1 -Action Uninstall        # removes only what cronixWG installed
```
