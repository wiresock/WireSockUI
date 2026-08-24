## WireSock UI 0.3.12

This maintenance release improves the Settings dialog in classic WireSockUI builds, particularly on Windows 7 and other systems using the non-UWP package.

### Install or upgrade

1. Install or update the matching-architecture [WireSock Secure Connect CLI/SDK](https://www.wiresock.net/).
2. Download the MSI below that matches your Windows version and architecture.
3. Run the installer as an administrator. It upgrades an existing WireSockUI installation in place.

> [!IMPORTANT]
> WireSockUI releases are intentionally unsigned. Windows may display **Unknown publisher**. Verify the MSI with its adjacent `.sha256` file before installation.

### Choose a package

| System | Package |
| --- | --- |
| Windows 7 SP1, x86 or x64 | Matching `no-uwp` MSI |
| Windows 8.1 or later, x86 or x64 | Matching `uwp` MSI for notifications and update checks, or `no-uwp` |
| Windows 11 on Arm | `win-arm64-uwp` or `win-arm64-no-uwp` |

### What’s new

- Removed the empty rows left by unavailable update and notification options in the classic/non-UWP Settings dialog.
- Kept the remaining settings evenly spaced while retaining compact button margins.
- Made the compaction DPI- and font-aware for consistent rendering on Windows 7 and newer Windows versions.
- Preserved the full UWP Settings layout unchanged.
- Added regression coverage for both classic and UWP layouts across supported build architectures.

### Notes

- WireSockUI requires the WireSock Secure Connect CLI/SDK and administrator privileges.
- The `no-uwp` x86/x64 packages support Windows 7 SP1; UWP packages require Windows 8.1 or later.
- All six MSIs and the modules inside them are unsigned by policy.
- Each MSI is accompanied by validation metadata and an SPDX SBOM. Every MSI, validation document, and SBOM has a SHA-256 sidecar and a GitHub provenance attestation.

**Full changelog:** [release-v0.3.10...release-v0.3.12](https://github.com/wiresock/WireSockUI/compare/release-v0.3.10...release-v0.3.12)
