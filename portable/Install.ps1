#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
    throw 'Use 64-bit PowerShell on Windows x64.'
}
if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'This package requires Windows x64 (AMD64).' }
$destination = Join-Path $env:ProgramFiles 'cronixWG'
$serviceName = 'cronixWGSplitTunnel'
if (Get-Process -Name 'WireSockUI','vkturn-core' -ErrorAction SilentlyContinue) {
    throw 'Close cronixWG and vkturn-core before installing/updating.'
}
# Check all shipped payload hashes before copying any executable.
foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Malformed SHA256SUMS.txt' }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') { throw 'Unsafe manifest path' }
    $file = Join-Path $PSScriptRoot $relative
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "Hash mismatch: $relative" }
}
foreach ($name in @('mullvad-split-tunnel.sys','wintun.dll')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $PSScriptRoot $name)
    if ($signature.Status -ne 'Valid') { throw "Invalid publisher signature on $name`: $($signature.Status)" }
}
$driverService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($driverService -and $driverService.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName
    $driverService.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(10))
}
if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne $destination.TrimEnd('\')) {
    if (Test-Path -LiteralPath $destination) {
        if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Destination is a reparse point' }
    } else { New-Item -ItemType Directory -Path $destination | Out-Null }
    # Keep elevated executables and the driver writable only by administrators.
    # /setowner is a separate icacls operation; ownership is set below.
    & icacls.exe $destination /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect the installation directory' }
    Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $destination -Recurse -Force
}
& icacls.exe $destination /reset /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot reset installation ACLs' }
& icacls.exe $destination /setowner '*S-1-5-32-544' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot set installation ownership' }
$driverPath = Join-Path $destination 'mullvad-split-tunnel.sys'
if ($driverService) {
    & sc.exe config $serviceName type= kernel start= demand binPath= "`"$driverPath`""
} else {
    & sc.exe create $serviceName type= kernel start= demand binPath= "`"$driverPath`"" DisplayName= 'cronixWG Split Tunnel'
}
if ($LASTEXITCODE -ne 0) { throw 'Cannot register split tunnel driver service' }
Write-Host "Installed. Start $destination\WireSockUI.exe (UAC prompt expected)."
Write-Host 'The split driver starts only when a profile has DisallowedApps. WireSock is not required.'
