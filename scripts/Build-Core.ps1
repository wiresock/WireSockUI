#requires -Version 5.1
<#
.SYNOPSIS
    Builds vkturn-core.exe (the VK TURN proxy from ./core) for the WireSock UI payload.

.DESCRIPTION
    The UI project copies ..\out\vkturn-core.exe into its output, so run this before
    building WireSockUI.sln. Requires Go (see core/go.mod for the version).
#>
param(
    [ValidateSet('amd64', '386', 'arm64')]
    [string] $GoArch = 'amd64'
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repositoryPath 'out\vkturn-core.exe'

$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = $GoArch

Push-Location (Join-Path $repositoryPath 'core')
try {
    & go build -trimpath -ldflags '-s -w' -o $outputPath ./client
    if ($LASTEXITCODE -ne 0) { throw "go build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Output "Built $outputPath"
