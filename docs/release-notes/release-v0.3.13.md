## WireSock UI 0.3.13

This maintenance release fixes elevated autorun on Windows 7, safely handles autorun tasks left by older WireSockUI installations, and removes a brief blank-window flash during shutdown.

### Install or upgrade

1. Install or update the matching-architecture [WireSock Secure Connect CLI/SDK](https://www.wiresock.net/).
2. Download the MSI below that matches your Windows version and architecture.
3. Close WireSockUI, then run the installer as an administrator. It upgrades an existing installation in place.

> [!IMPORTANT]
> WireSockUI releases are intentionally unsigned. Windows may display **Unknown publisher**. Verify the MSI with its adjacent `.sha256` file before installation.

### Choose a package

| System | Package |
| --- | --- |
| Windows 7 SP1, x86 or x64 | Matching `no-uwp` MSI |
| Windows 8.1 or later, x86 or x64 | Matching `uwp` MSI for notifications and update checks, or `no-uwp` |
| Windows 11 on Arm | `win-arm64-uwp` or `win-arm64-no-uwp` |

### What’s fixed

- Restored **Run when Windows starts** on Windows 7 by generating a Task Scheduler 2.1/schema 1.3 definition instead of using Windows 8-only task settings.
- Recognized the exact historical WireSockUI autorun-task shape when it points to an older installation path, and offered an explicit, safety-checked migration to the current installation.
- Continued to leave foreign or modified scheduled tasks untouched.
- Preserved the real Task Scheduler error when an elevated helper fails, rather than misreporting every verified failure as a timeout.
- Prevented a partially completed legacy-task migration from being reported as successful.
- Prevented the main window or a notification activation from briefly recreating a blank window while WireSockUI is closing.
- Added regression coverage for Windows 7 task definitions, legacy-task migration and consent, timeout verification, and shutdown activation guards.

The Windows 7 x64 `no-uwp` package was manually validated against both reported autorun scenarios before release.

### Notes

- WireSockUI requires the WireSock Secure Connect CLI/SDK and administrator privileges.
- The `no-uwp` x86/x64 packages support Windows 7 SP1; UWP packages require Windows 8.1 or later.
- All six MSIs and the modules inside them are unsigned by policy.
- Each MSI is accompanied by validation metadata and an SPDX SBOM. Every MSI, validation document, and SBOM has a SHA-256 sidecar and a GitHub provenance attestation.

**Full changelog:** [release-v0.3.12...release-v0.3.13](https://github.com/wiresock/WireSockUI/compare/release-v0.3.12...release-v0.3.13)
