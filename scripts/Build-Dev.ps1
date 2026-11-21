#requires -Version 5.1
<#
.SYNOPSIS
    Builds a runnable local cronixWG: vkturn-core.exe, then the WireSock UI project.

.DESCRIPTION
    Build the UI *project*, not WireSockUI.sln, when you want to run the result from bin\.
    The native launcher embeds the size and SHA-256 of every payload file after the
    build strips Authenticode signatures. Building the solution also builds the test
    project, whose reference build copies the original (signed, larger) NuGet DLLs back
    into the same bin folder afterwards, and the launcher then refuses to start with
    "A payload file changed size during validation".

.EXAMPLE
    .\scripts\Build-Dev.ps1
    .\scripts\Build-Dev.ps1 -Configuration Release -SkipCore
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64',

    [string] $Version = '0.3.0',

    [switch] $SkipCore
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot

if (-not $SkipCore) {
    $goArch = @{ x64 = 'amd64'; x86 = '386'; ARM64 = 'arm64' }[$Platform]
    & (Join-Path $PSScriptRoot 'Build-Core.ps1') -GoArch $goArch
}

$project = Join-Path $repositoryPath 'WireSockUI\WireSockUI.csproj'
& dotnet build $project --configuration $Configuration "-p:Platform=$Platform" `
    -p:UseSharedCompilation=false "-p:Version=$Version" -m:1
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$output = Join-Path $repositoryPath "bin\$Platform\$Configuration\net472-windows\WireSockUI.exe"
Write-Output "Run: $output"
