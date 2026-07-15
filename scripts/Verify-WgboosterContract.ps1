param(
    [Parameter(Mandatory = $true)]
    [string]$SdkRoot
)

$ErrorActionPreference = 'Stop'
$sdkRootPath = (Resolve-Path -LiteralPath $SdkRoot).Path
$headerPath = Join-Path $sdkRootPath 'include\wgbooster.h'
$definitionPath = Join-Path $sdkRootPath 'wgbooster\wgbooster.def'

if (-not (Test-Path -LiteralPath $headerPath -PathType Leaf)) {
    throw "WireSock SDK header was not found at '$headerPath'."
}
if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
    throw "WireSock SDK export definition was not found at '$definitionPath'."
}

$requiredExports = @(
    'wgb_get_handle_ex',
    'wgb_release_handle',
    'wgb_set_log_level',
    'wgb_create_tunnel_from_file_w',
    'wgb_drop_tunnel',
    'wgb_start_tunnel',
    'wgb_stop_tunnel',
    'wgb_get_tunnel_state',
    'wgb_get_tunnel_active',
    'wgb_set_network_lock_mode',
    'wgb_get_network_lock_mode',
    'wgbp_get_handle_ex',
    'wgbp_release_handle',
    'wgbp_set_log_level',
    'wgbp_create_tunnel_from_file_w',
    'wgbp_drop_tunnel',
    'wgbp_start_tunnel',
    'wgbp_stop_tunnel',
    'wgbp_get_tunnel_state',
    'wgbp_get_tunnel_active',
    'wgbp_set_network_lock_mode',
    'wgbp_get_network_lock_mode',
    'wg_reset_network_lock',
    'wg_is_network_lock_active'
)

$definition = Get-Content -LiteralPath $definitionPath -Raw
$header = Get-Content -LiteralPath $headerPath -Raw
$missingExports = @($requiredExports | Where-Object {
    $definition -notmatch "(?m)^\s*$([regex]::Escape($_))(?:\s|$)"
})
$missingDeclarations = @($requiredExports | Where-Object {
    $header -notmatch "\b$([regex]::Escape($_))\s*\("
})

if ($missingExports.Count -gt 0) {
    throw "wgbooster.def is missing required WireSock UI exports: $($missingExports -join ', ')"
}
if ($missingDeclarations.Count -gt 0) {
    throw "wgbooster.h is missing required WireSock UI declarations: $($missingDeclarations -join ', ')"
}

$signatureChecks = [ordered]@{
    'wgb_log_level values' =
        'enum\s+wgb_log_level\s*\{[^}]*error\s*=\s*0[^}]*warning\s*=\s*1[^}]*info\s*=\s*2[^}]*debug\s*=\s*4[^}]*all\s*=\s*255[^}]*\}'
    'wgb_network_lock_mode values' =
        'enum\s+wgb_network_lock_mode\s*\{[^}]*wgb_network_lock_disabled\s*=\s*0[^}]*wgb_network_lock_enabled\s*=\s*1[^}]*\}'
    'wgb_stats layout' =
        'struct\s+wgb_stats\s*\{\s*int64_t\s+\w+\s*;\s*uint64_t\s+\w+\s*;\s*uint64_t\s+\w+\s*;\s*float\s+\w+\s*;\s*int32_t\s+\w+\s*;[^}]*\}'
    'wg_reset_network_lock signature' =
        'BOOL\s+__stdcall\s+wg_reset_network_lock\s*\(\s*\)'
    'wg_is_network_lock_active signature' =
        'BOOL\s+__stdcall\s+wg_is_network_lock_active\s*\(\s*\)'
}

foreach ($prefix in @('wgb', 'wgbp')) {
    $escapedPrefix = [regex]::Escape($prefix)
    $signatureChecks["$prefix get_handle_ex signature"] =
        "HANDLE\s+__stdcall\s+${escapedPrefix}_get_handle_ex\s*\(\s*void\s*\(\s*\*\s*\w+\s*\)\s*\(\s*const\s+char\s*\*\s*\)\s*,\s*wgb_log_level\s+\w+\s*,\s*void\s*\(\s*\*\s*\w+\s*\)\s*\(\s*wg_tunnel_event\s*\)\s*,\s*const\s+bool\s+\w+\s*,\s*const\s+bool\s+\w+\s*\)"
    $signatureChecks["$prefix release_handle signature"] =
        "void\s+__stdcall\s+${escapedPrefix}_release_handle\s*\(\s*HANDLE\s+\w+\s*\)"
    $signatureChecks["$prefix set_log_level signature"] =
        "void\s+__stdcall\s+${escapedPrefix}_set_log_level\s*\(\s*HANDLE\s+\w+\s*,\s*wgb_log_level\s+\w+\s*\)"
    $signatureChecks["$prefix create_tunnel_from_file_w signature"] =
        "BOOL\s+__stdcall\s+${escapedPrefix}_create_tunnel_from_file_w\s*\(\s*HANDLE\s+\w+\s*,\s*const\s+wchar_t\s*\*\s*\w+\s*\)"
    $signatureChecks["$prefix drop_tunnel signature"] =
        "BOOL\s+__stdcall\s+${escapedPrefix}_drop_tunnel\s*\(\s*HANDLE\s+\w+\s*,\s*BOOL\s+\w+\s*\)"
    $signatureChecks["$prefix start_tunnel signature"] =
        "BOOL\s+__stdcall\s+${escapedPrefix}_start_tunnel\s*\(\s*HANDLE\s+\w+\s*\)"
    $signatureChecks["$prefix stop_tunnel signature"] =
        "BOOL\s+__stdcall\s+${escapedPrefix}_stop_tunnel\s*\(\s*HANDLE\s+\w+\s*\)"
    $signatureChecks["$prefix get_tunnel_state signature"] =
        "wgb_stats\s+__stdcall\s+${escapedPrefix}_get_tunnel_state\s*\(\s*HANDLE\s+\w+\s*\)"
    $signatureChecks["$prefix get_tunnel_active signature"] =
        "BOOL\s+__stdcall\s+${escapedPrefix}_get_tunnel_active\s*\(\s*HANDLE\s+\w+\s*\)"
    $signatureChecks["$prefix set_network_lock_mode signature"] =
        "BOOL\s+__stdcall\s+${escapedPrefix}_set_network_lock_mode\s*\(\s*HANDLE\s+\w+\s*,\s*wgb_network_lock_mode\s+\w+\s*\)"
    $signatureChecks["$prefix get_network_lock_mode signature"] =
        "wgb_network_lock_mode\s+__stdcall\s+${escapedPrefix}_get_network_lock_mode\s*\(\s*HANDLE\s+\w+\s*\)"
}

foreach ($check in $signatureChecks.GetEnumerator()) {
    if ($header -notmatch $check.Value) {
        throw "WireSock SDK contract check failed for $($check.Key)."
    }
}

Write-Host "WireSock SDK header/export contract is compatible with WireSock UI ($($requiredExports.Count) exports checked)."
