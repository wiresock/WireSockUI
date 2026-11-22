#!/usr/bin/env python3
"""Fetch unmodified, hash-pinned Windows x64 drivers from their publishers."""
import hashlib
import io
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
BINARY_REV = "80c2e94d5ee8087ea80059ab24f1a81b632714f7"
SOURCE_REV = "0a0eb97f67d1dbcb3d08bda66d3b24f465d95475"
BASE = f"https://raw.githubusercontent.com/mullvad/mullvadvpn-app-binaries/{BINARY_REV}/x86_64-pc-windows-msvc/split-tunnel/"
FILES = {
    "mullvad-split-tunnel.sys": "10cf25bbcfe51fd663a1fec88a98e9b858f3a579589bb2ec496b66e4fdd1b201",
    "mullvad-split-tunnel.cat": "c599926a0327d7ae06b534f4cd039db30392e1897bb9d03e4fec3631744a4e6d",
    "mullvad-split-tunnel.inf": "3dd5905e5fb98d61a942a33e8c9a5ba07c3a2de1e4f319e1fec3e54df6591608",
}

def checked(data, digest):
    if hashlib.sha256(data).hexdigest() != digest:
        raise RuntimeError("Publisher download SHA-256 mismatch")
    return data

def download(url):
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read()

def fetch(url, path, digest):
    if path.exists() and hashlib.sha256(path.read_bytes()).hexdigest() == digest:
        return
    data = checked(download(url), digest)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)

def main():
    for name, digest in FILES.items():
        fetch(BASE + name, ROOT / "third_party/mullvad" / name, digest)
    fetch(f"https://raw.githubusercontent.com/mullvad/win-split-tunnel/{SOURCE_REV}/LICENSE-MPL.txt",
          ROOT / "third_party/mullvad/LICENSE-MPL.txt",
          "66a3107d5ad6a058aab753eaac2047ccb2ed0e39465dd0fe5844da3e300d5172")
    dll = ROOT / "third_party/wintun/wintun.dll"
    if not dll.exists() or not (dll.parent / "LICENSE.txt").exists() or hashlib.sha256(dll.read_bytes()).hexdigest() != "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce":
        archive = checked(download("https://www.wintun.net/builds/wintun-0.14.1.zip"),
                          "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51")
        with zipfile.ZipFile(io.BytesIO(archive)) as z:
            dll.parent.mkdir(parents=True, exist_ok=True)
            dll.write_bytes(z.read("wintun/bin/amd64/wintun.dll"))
            (dll.parent / "LICENSE.txt").write_bytes(z.read("wintun/LICENSE.txt"))
    checked(dll.read_bytes(), "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce")
    print("Verified Mullvad split tunnel 1.3.0.0 and Wintun 0.14.1 (Windows x64)")

if __name__ == "__main__":
    main()
