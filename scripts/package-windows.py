#!/usr/bin/env python3
"""Assemble a clean Windows folder and ZIP; never strip driver signatures."""
import hashlib
from pathlib import Path
import shutil
import sys
import zipfile

root = Path(__file__).resolve().parent.parent
build = Path(sys.argv[1]).resolve()
out = root / "out/windows-x64"
# Only replace this script's generated output, never source or user profiles.
if out.exists():
    shutil.rmtree(out)
shutil.copytree(build / "ui", out)
shutil.copy2(build / "vkturn-core.exe", out / "vkturn-core.exe")
shutil.copy2(root / "third_party/wintun/wintun.dll", out / "wintun.dll")
for name in ("mullvad-split-tunnel.sys", "mullvad-split-tunnel.cat", "mullvad-split-tunnel.inf"):
    shutil.copy2(root / "third_party/mullvad" / name, out / name)
for name in ("Install.ps1", "Uninstall-Driver.ps1", "WINDOWS-TESTING.txt"):
    shutil.copy2(root / "portable" / name, out / name)
licenses = out / "licenses"
licenses.mkdir()
for src, dst in ((root / "LICENSE", "UI-MIT.txt"),
                 (root / "core/LICENSE", "vk-turn-proxy-LICENSE.txt"),
                 (root / "third_party/mullvad/LICENSE-MPL.txt", "Mullvad-MPL-2.0.txt"),
                 (root / "third_party/wintun/LICENSE.txt", "Wintun-LICENSE.txt")):
    shutil.copy2(src, licenses / dst)
revision = "Mullvad binaries: 80c2e94d5ee8087ea80059ab24f1a81b632714f7\nMullvad driver source/ABI: 0a0eb97f67d1dbcb3d08bda66d3b24f465d95475\nWintun: 0.14.1\n"
(out / "DRIVER-VERSIONS.txt").write_text(revision)
files = sorted(p for p in out.rglob("*") if p.is_file())
(out / "SHA256SUMS.txt").write_text("".join(
    f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(out).as_posix()}\n" for p in files))
archive = root / "out/cronixWG-windows-x64.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for p in sorted(out.rglob("*")):
        if p.is_file(): z.write(p, Path("cronixWG") / p.relative_to(out))
print(archive)
