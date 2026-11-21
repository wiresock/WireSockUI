#requires -Version 5.1
<#
.SYNOPSIS
    Installs, removes or reports the WireSock drivers that cronixWG needs without the WireSock CLI.

.DESCRIPTION
    wgbooster.dll (loaded by the UI) talks to the NDIS lightweight filter driver "ndiswgc"
    (device \\.\NDISWGC). This script installs that filter from third_party\wiresock\drivers
    and, optionally, the WireSock virtual network adapter used by VirtualAdapterMode.

    Ownership: the script only removes drivers it installed itself. If the filter is already
    present (for example from a WireSock Secure Connect installation) Install leaves it alone
    and Uninstall will not touch it unless -Force is given, so an existing WireSock setup is
    never broken. Ownership is recorded under HKLM\SOFTWARE\cronixWG.

    The .sys/.inf/.cat files must stay byte-identical: the signature lives in the .cat file.

.PARAMETER Action
    Status (default), Install or Uninstall.

.PARAMETER VirtualAdapter
    Also install/remove the WireSock virtual adapter (only needed for VirtualAdapterMode).

.PARAMETER DriverRoot
    Folder with drivers\, wiresock-adapter\ and certificates\. Defaults to third_party\wiresock.

.PARAMETER Force
    Uninstall even drivers that this script did not install.

.EXAMPLE
    .\scripts\WireSockDriver.ps1
    .\scripts\WireSockDriver.ps1 -Action Install
    .\scripts\WireSockDriver.ps1 -Action Uninstall -VirtualAdapter
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Install', 'Uninstall')]
    [string] $Action = 'Status',

    [switch] $VirtualAdapter,

    [string] $DriverRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'third_party\wiresock'),

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$FilterComponentId = 'nt_ndiswgc'
$FilterServiceName = 'ndiswgc'
$FilterInfName = 'ndiswgc_lwf.inf'
$NetServiceClassGuid = '{4D36E974-E325-11CE-BFC1-08002BE10318}'
$AdapterHardwareId = 'WireSock'
$AdapterInfName = 'wiresock.inf'
$OwnershipKey = 'HKLM:\SOFTWARE\cronixWG'
$NetcfgRebootRequired = 0x0004A020   # NETCFG_S_REBOOT

function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DriverFlavorFolder {
    param([string] $Base)
    # WireSock CLI layout: drivers\win10 (attestation-signed, Windows 10/11), drivers\win8 (8/8.1),
    # drivers\win7; wiresock-adapter\win10 and wiresock-adapter\legacy (7/8.x).
    # Older Secure Connect copies kept the 7/8 build directly in the base folder.
    $version = [Environment]::OSVersion.Version
    $candidates = if ($version.Major -ge 10) { @('win10') }
                  elseif ($version.Major -eq 6 -and $version.Minor -ge 2) { @('win8', 'legacy') }
                  else { @('win7', 'legacy') }
    foreach ($name in $candidates) {
        $folder = Join-Path $Base $name
        if (Test-Path -LiteralPath $folder -PathType Container) { return $folder }
    }
    return $Base
}

function Get-FilterBinding {
    # Network components installed through INetCfg live under the class key with ComponentId.
    $classKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Network\$NetServiceClassGuid"
    if (-not (Test-Path -LiteralPath $classKey)) { return $null }
    foreach ($child in Get-ChildItem -LiteralPath $classKey -ErrorAction SilentlyContinue) {
        $props = Get-ItemProperty -LiteralPath $child.PSPath -ErrorAction SilentlyContinue
        if ($props -and ($props.PSObject.Properties.Name -contains 'ComponentId') -and
            $props.ComponentId -eq $FilterComponentId) {
            return $props
        }
    }
    return $null
}

function Get-StorePackages {
    param([string] $OriginalName)
    # Get-WindowsDriver is locale-independent (pnputil output is translated).
    if (-not (Test-Administrator)) { return @() }
    return @(Get-WindowsDriver -Online -ErrorAction SilentlyContinue |
        Where-Object { [IO.Path]::GetFileName($_.OriginalFileName) -ieq $OriginalName })
}

function Get-AdapterDevices {
    return @(Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.HardwareID -contains $AdapterHardwareId -or $_.HardwareID -contains "root\$AdapterHardwareId" })
}

function Get-Ownership {
    if (-not (Test-Path -LiteralPath $OwnershipKey)) { return @{ Filter = $false; Adapter = $false } }
    $p = Get-ItemProperty -LiteralPath $OwnershipKey
    return @{
        Filter  = ($p.PSObject.Properties.Name -contains 'FilterInstalledByCronix') -and $p.FilterInstalledByCronix -eq 1
        Adapter = ($p.PSObject.Properties.Name -contains 'AdapterInstalledByCronix') -and $p.AdapterInstalledByCronix -eq 1
    }
}

function Set-Ownership {
    param([string] $Name, [bool] $Value)
    if (-not (Test-Path -LiteralPath $OwnershipKey)) { New-Item -Path $OwnershipKey -Force | Out-Null }
    if ($Value) {
        New-ItemProperty -LiteralPath $OwnershipKey -Name $Name -Value 1 -PropertyType DWord -Force | Out-Null
    }
    else {
        Remove-ItemProperty -LiteralPath $OwnershipKey -Name $Name -ErrorAction SilentlyContinue
    }
}

function Invoke-Tool {
    param([string] $FilePath, [string[]] $Arguments)
    Write-Verbose "$FilePath $($Arguments -join ' ')"
    $output = & $FilePath @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($output.Trim()) { Write-Verbose $output.Trim() }
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

function Assert-DriverFiles {
    param([string] $Folder, [string[]] $Names)
    foreach ($name in $Names) {
        $path = Join-Path $Folder $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing driver file '$path'. Copy the WireSock drivers into '$DriverRoot' first."
        }
    }
}

function Import-LegacyRootCertificate {
    # Windows 7/8.x need the issuing root to validate the (non-attestation) driver signature.
    if ([Environment]::OSVersion.Version.Major -ge 10) { return }
    $cer = Join-Path $DriverRoot 'certificates\rootac.cer'
    if (Test-Path -LiteralPath $cer) {
        Write-Output "Importing $cer into LocalMachine\Root for Windows $([Environment]::OSVersion.Version)."
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    }
}

function Show-Status {
    $binding = Get-FilterBinding
    $service = Get-Service -Name $FilterServiceName -ErrorAction SilentlyContinue
    $own = Get-Ownership
    $adapters = @(Get-AdapterDevices)

    [pscustomobject]@{
        'Filter driver (nt_ndiswgc)' = if ($binding) { 'installed' } else { 'not installed' }
        'Filter service'             = if ($service) { "$($service.Status)" } else { 'absent' }
        'Filter installed by'        = if (-not $binding) { '-' } elseif ($own.Filter) { 'cronixWG' } else { 'another product (e.g. WireSock)' }
        'Virtual adapter'            = if ($adapters.Count) { "$($adapters.Count) device(s): $(($adapters | ForEach-Object Status) -join ', ')" } else { 'not installed' }
        'Adapter installed by'       = if (-not $adapters.Count) { '-' } elseif ($own.Adapter) { 'cronixWG' } else { 'another product' }
        'Driver store packages'      = if (Test-Administrator) {
            $pkgs = @(Get-StorePackages $FilterInfName) + @(Get-StorePackages $AdapterInfName)
            if ($pkgs.Count) { ($pkgs | ForEach-Object { "$($_.Driver) ($([IO.Path]::GetFileName($_.OriginalFileName)) $($_.Version))" }) -join '; ' } else { 'none' }
        } else { '(run as administrator to list)' }
        'Bundled driver folder'      = if (Test-Path -LiteralPath $DriverRoot) { $DriverRoot } else { "$DriverRoot (missing)" }
    } | Format-List
}

function Install-Filter {
    if (Get-FilterBinding) {
        Write-Output 'The ndiswgc filter is already installed; leaving it as is.'
        return
    }

    $folder = Get-DriverFlavorFolder (Join-Path $DriverRoot 'drivers')
    Assert-DriverFiles $folder @($FilterInfName, 'ndiswgc.sys', 'ndiswgc.cat')
    Import-LegacyRootCertificate

    $inf = Join-Path $folder $FilterInfName
    Write-Output "Installing the ndiswgc filter from $inf (network connections may blink)..."
    $netcfg = Join-Path $env:SystemRoot 'System32\netcfg.exe'
    $result = Invoke-Tool $netcfg @('-v', '-l', $inf, '-c', 's', '-i', $FilterComponentId)
    if ($result.ExitCode -ne 0 -and $result.ExitCode -ne $NetcfgRebootRequired) {
        throw "netcfg failed with exit code $($result.ExitCode):`n$($result.Output)"
    }

    if (-not (Get-FilterBinding)) {
        throw "netcfg reported success but the $FilterComponentId component is not registered."
    }

    Set-Ownership 'FilterInstalledByCronix' $true
    Write-Output 'ndiswgc filter installed.'
    if ($result.ExitCode -eq $NetcfgRebootRequired) { Write-Warning 'Windows asks for a reboot to finish the installation.' }
}

function Uninstall-Filter {
    $own = Get-Ownership
    if (-not (Get-FilterBinding)) {
        Write-Output 'The ndiswgc filter is not installed.'
        Set-Ownership 'FilterInstalledByCronix' $false
        return
    }
    if (-not $own.Filter -and -not $Force) {
        Write-Output 'The ndiswgc filter was installed by another product (WireSock?); not removing it. Use -Force to override.'
        return
    }

    Write-Output 'Removing the ndiswgc filter (network connections may blink)...'
    $netcfg = Join-Path $env:SystemRoot 'System32\netcfg.exe'
    $result = Invoke-Tool $netcfg @('-v', '-u', $FilterComponentId)
    if ($result.ExitCode -ne 0 -and $result.ExitCode -ne $NetcfgRebootRequired) {
        throw "netcfg -u failed with exit code $($result.ExitCode):`n$($result.Output)"
    }

    foreach ($pkg in Get-StorePackages $FilterInfName) {
        Write-Output "Removing $($pkg.Driver) from the driver store."
        $r = Invoke-Tool (Join-Path $env:SystemRoot 'System32\pnputil.exe') @('/delete-driver', $pkg.Driver, '/uninstall')
        if ($r.ExitCode -ne 0) { Write-Warning "pnputil could not delete $($pkg.Driver): $($r.Output.Trim())" }
    }

    Set-Ownership 'FilterInstalledByCronix' $false
    Write-Output 'ndiswgc filter removed.'
    if ($result.ExitCode -eq $NetcfgRebootRequired) { Write-Warning 'Windows asks for a reboot to finish the removal.' }
}

function Install-Adapter {
    if (@(Get-AdapterDevices).Count -gt 0) {
        Write-Output 'The WireSock virtual adapter is already installed; leaving it as is.'
        return
    }

    $base = Join-Path $DriverRoot 'wiresock-adapter'
    $folder = Get-DriverFlavorFolder $base
    Assert-DriverFiles $folder @($AdapterInfName, 'wiresock.sys', 'wiresock.cat')
    Assert-DriverFiles $base @('devcon.exe')

    $inf = Join-Path $folder $AdapterInfName
    Write-Output "Installing the WireSock virtual adapter from $inf..."
    $result = Invoke-Tool (Join-Path $base 'devcon.exe') @('install', $inf, $AdapterHardwareId)
    if ($result.ExitCode -ne 0 -and $result.ExitCode -ne 1) {   # devcon: 1 = reboot required
        throw "devcon install failed with exit code $($result.ExitCode):`n$($result.Output)"
    }

    Set-Ownership 'AdapterInstalledByCronix' $true
    Write-Output 'WireSock virtual adapter installed.'
}

function Uninstall-Adapter {
    $devices = @(Get-AdapterDevices)
    if ($devices.Count -eq 0) {
        Write-Output 'The WireSock virtual adapter is not installed.'
        Set-Ownership 'AdapterInstalledByCronix' $false
        return
    }
    if (-not (Get-Ownership).Adapter -and -not $Force) {
        Write-Output 'The WireSock virtual adapter belongs to another product; not removing it. Use -Force to override.'
        return
    }

    $devcon = Join-Path $DriverRoot 'wiresock-adapter\devcon.exe'
    Assert-DriverFiles (Split-Path $devcon) @('devcon.exe')
    $result = Invoke-Tool $devcon @('remove', $AdapterHardwareId)
    if ($result.ExitCode -ne 0 -and $result.ExitCode -ne 1) {
        throw "devcon remove failed with exit code $($result.ExitCode):`n$($result.Output)"
    }

    foreach ($pkg in Get-StorePackages $AdapterInfName) {
        Write-Output "Removing $($pkg.Driver) from the driver store."
        $r = Invoke-Tool (Join-Path $env:SystemRoot 'System32\pnputil.exe') @('/delete-driver', $pkg.Driver)
        if ($r.ExitCode -ne 0) { Write-Warning "pnputil could not delete $($pkg.Driver): $($r.Output.Trim())" }
    }

    Set-Ownership 'AdapterInstalledByCronix' $false
    Write-Output 'WireSock virtual adapter removed.'
}

if ($Action -ne 'Status' -and -not (Test-Administrator)) {
    throw 'Installing or removing drivers requires an elevated (Run as administrator) PowerShell.'
}

switch ($Action) {
    'Status' { Show-Status }
    'Install' {
        Install-Filter
        if ($VirtualAdapter) { Install-Adapter }
        Show-Status
    }
    'Uninstall' {
        if ($VirtualAdapter) { Uninstall-Adapter }
        Uninstall-Filter
        Show-Status
    }
}
