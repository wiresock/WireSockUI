#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
# .NET 8+ SDK can cross-build .NET Framework using reference assemblies.
# DOTNET permits a private SDK installation without changing system tooling.
dotnet_cmd="${DOTNET:-dotnet}"
build_dir="$(mktemp -d /tmp/cronixwg-build.XXXXXX)"
output_dir="$repo_dir/out/windows-x64"
python3 "$repo_dir/scripts/fetch-windows-deps.py"
(
    cd "$repo_dir/core"
    go test ./client ./tcputil ./server
    GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -o "$build_dir/vkturn-core.exe" ./client
)
# The existing Windows build pins a newer SDK in global.json. Running outside
# the repository allows the supplied Linux SDK to build this separate project.
(
    cd "$build_dir"
    "$dotnet_cmd" build "$repo_dir/portable/Portable.csproj" -c Release \
        -o "$build_dir/ui" -p:UseSharedCompilation=false
)
python3 "$repo_dir/scripts/package-windows.py" "$build_dir"
printf 'Windows package: %s\n' "$output_dir"
