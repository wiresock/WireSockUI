#requires -Version 5.1
#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'WireSockUI','vkturn-core' -ErrorAction SilentlyContinue) {
    throw 'Close cronixWG before removing its driver service.'
}
$service = Get-Service -Name 'cronixWGSplitTunnel' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $service.Name
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(10))
    }
    & sc.exe delete cronixWGSplitTunnel
    if ($LASTEXITCODE -ne 0) { throw 'Cannot remove cronixWG driver service' }
}
Write-Host 'cronixWG driver service removed. Existing profiles and other VPN drivers were preserved.'
