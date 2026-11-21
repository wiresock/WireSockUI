[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $LauncherPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string] $ExpectedPlatform,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z')]
    [string] $ExpectedVersion,

    [switch] $AllowDevelopmentBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'NativeBootstrap.Validation.psm1'
Import-Module -Name $modulePath -Force -ErrorAction Stop

function Get-NativeValidationTestLayout {
    param([byte[]]$Bytes)

    if ($Bytes.Length -lt 512) {
        throw 'The native launcher is too small for validator corruption tests.'
    }
    $peOffset = [int][BitConverter]::ToUInt32($Bytes, 0x3c)
    $optionalOffset = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($Bytes, $optionalOffset)
    $dataDirectoryOffset = if ($magic -eq 0x010b) {
        $optionalOffset + 96
    }
    elseif ($magic -eq 0x020b) {
        $optionalOffset + 112
    }
    else {
        throw 'The native launcher has an unsupported optional-header format.'
    }
    $sectionCount = [int][BitConverter]::ToUInt16($Bytes, $peOffset + 6)
    $optionalSize = [int][BitConverter]::ToUInt16($Bytes, $peOffset + 20)
    $sectionTableOffset = $optionalOffset + $optionalSize
    $sections = New-Object 'System.Collections.Generic.List[object]'
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $sectionOffset = $sectionTableOffset + ($index * 40)
        $sections.Add([pscustomobject]@{
            VirtualSize = [UInt32][BitConverter]::ToUInt32($Bytes, $sectionOffset + 8)
            VirtualAddress = [UInt32][BitConverter]::ToUInt32($Bytes, $sectionOffset + 12)
            RawSize = [UInt32][BitConverter]::ToUInt32($Bytes, $sectionOffset + 16)
            RawPointer = [UInt32][BitConverter]::ToUInt32($Bytes, $sectionOffset + 20)
        })
    }

    $loadConfigRva = [UInt32][BitConverter]::ToUInt32(
        $Bytes,
        $dataDirectoryOffset + (10 * 8))
    $loadConfigOffset = $null
    foreach ($section in $sections) {
        [UInt64]$span = if ($section.VirtualSize -eq 0) {
            $section.RawSize
        }
        else {
            $section.VirtualSize
        }
        if ($loadConfigRva -ge $section.VirtualAddress -and
            [UInt64]$loadConfigRva -lt [UInt64]$section.VirtualAddress + $span) {
            $loadConfigOffset = [int](
                $section.RawPointer +
                ($loadConfigRva - $section.VirtualAddress))
            break
        }
    }
    if ($null -eq $loadConfigOffset) {
        throw 'Unable to locate the native load configuration for corruption tests.'
    }

    return [pscustomobject]@{
        OptionalOffset = $optionalOffset
        DataDirectoryOffset = $dataDirectoryOffset
        LoadConfigOffset = $loadConfigOffset
        Magic = $magic
    }
}

function Set-UInt16InBytes {
    param([byte[]]$Bytes, [int]$Offset, [UInt16]$Value)

    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function Set-UInt32InBytes {
    param([byte[]]$Bytes, [int]$Offset, [UInt32]$Value)

    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function Get-ByteSequenceCount {
    param(
        [byte[]]$Bytes,
        [byte[]]$Sequence
    )

    if ($Sequence.Length -eq 0 -or $Sequence.Length -gt $Bytes.Length) {
        return 0
    }

    $count = 0
    for ($offset = 0;
        $offset -le $Bytes.Length - $Sequence.Length;
        $offset++) {
        if ($Bytes[$offset] -ne $Sequence[0]) {
            continue
        }

        $matches = $true
        for ($index = 1; $index -lt $Sequence.Length; $index++) {
            if ($Bytes[$offset + $index] -eq $Sequence[$index]) {
                continue
            }
            $matches = $false
            break
        }
        if ($matches) {
            ++$count
        }
    }
    return $count
}

function Assert-NativeValidatorRejectsMutation {
    param(
        [string]$Name,
        [byte[]]$OriginalBytes,
        [scriptblock]$Mutation
    )

    $mutatedBytes = New-Object byte[] $OriginalBytes.Length
    [Array]::Copy($OriginalBytes, $mutatedBytes, $OriginalBytes.Length)
    & $Mutation $mutatedBytes

    $temporaryPath = Join-Path ([IO.Path]::GetTempPath()) (
        'WireSockUI.NativeValidator.' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        [IO.File]::WriteAllBytes($temporaryPath, $mutatedBytes)
        $rejected = $false
        try {
            Assert-NativeBootstrap `
                -Path $temporaryPath `
                -ExpectedPlatform $ExpectedPlatform `
                -ExpectedVersion $ExpectedVersion `
                -RequireProductionBuild:(-not $AllowDevelopmentBuild) |
                Out-Null
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Native bootstrap validator accepted corruption of $Name."
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

$result = Assert-NativeBootstrap `
    -Path $LauncherPath `
    -ExpectedPlatform $ExpectedPlatform `
    -ExpectedVersion $ExpectedVersion `
    -RequireProductionBuild:(-not $AllowDevelopmentBuild)

$launcherFile = Get-Item -LiteralPath $result.Path -Force
if ($launcherFile.Length -gt 16MB) {
    throw 'The native launcher is too large for bounded corruption tests.'
}
$launcherBytes = [IO.File]::ReadAllBytes($result.Path)
$testLayout = Get-NativeValidationTestLayout -Bytes $launcherBytes
$nativeHostSelfTestToken = 'WireSockUI.NativeHostSelfTest.v1'
$nativeHostSelfTestCommand =
    '--native-host-self-test "argument with spaces"'
foreach ($literal in @(
        $nativeHostSelfTestToken,
        $nativeHostSelfTestCommand)) {
    $literalBytes = [Text.Encoding]::Unicode.GetBytes($literal + [char]0)
    if ((Get-ByteSequenceCount `
                -Bytes $launcherBytes `
                -Sequence $literalBytes) -ne 1) {
        throw "Native launcher does not contain exactly one canonical '$literal' self-test contract literal."
    }
}

$managedProgramPath = Join-Path (
    Split-Path -Parent $PSScriptRoot) 'WireSockUI\Program.cs'
$managedProgramSource = [IO.File]::ReadAllText(
    $managedProgramPath,
    [Text.UTF8Encoding]::new($false, $true))
$managedTokenPattern =
    'internal const string NativeHostSelfTestToken = "' +
    [regex]::Escape($nativeHostSelfTestToken) +
    '";'
if ([regex]::Matches(
        $managedProgramSource,
        $managedTokenPattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant).Count -ne 1) {
    throw 'Managed and native launchers do not share one exact self-test token.'
}

Assert-NativeValidatorRejectsMutation `
    -Name 'the base-relocation directory' `
    -OriginalBytes $launcherBytes `
    -Mutation {
        param($bytes)
        [Array]::Clear($bytes, $testLayout.DataDirectoryOffset + (5 * 8), 8)
    }
Assert-NativeValidatorRejectsMutation `
    -Name 'the Control Flow Guard table flags' `
    -OriginalBytes $launcherBytes `
    -Mutation {
        param($bytes)
        $guardFlagsOffset = if ($testLayout.Magic -eq 0x010b) { 0x58 } else { 0x90 }
        Set-UInt32InBytes `
            -Bytes $bytes `
            -Offset ($testLayout.LoadConfigOffset + $guardFlagsOffset) `
            -Value 0
    }
Assert-NativeValidatorRejectsMutation `
    -Name 'the Control Flow Guard DLL characteristic' `
    -OriginalBytes $launcherBytes `
    -Mutation {
        param($bytes)
        $characteristicsOffset = $testLayout.OptionalOffset + 0x46
        [UInt16]$characteristics =
            [BitConverter]::ToUInt16($bytes, $characteristicsOffset)
        Set-UInt16InBytes `
            -Bytes $bytes `
            -Offset $characteristicsOffset `
            -Value ([UInt16]($characteristics -band 0xbfff))
    }
if ($ExpectedPlatform -eq 'x86') {
    Assert-NativeValidatorRejectsMutation `
        -Name 'the SafeSEH table count' `
        -OriginalBytes $launcherBytes `
        -Mutation {
            param($bytes)
            Set-UInt32InBytes `
                -Bytes $bytes `
                -Offset ($testLayout.LoadConfigOffset + 0x44) `
                -Value 0
        }
}
# cronixWG: the launcher intentionally has no CETCOMPAT record (see Build-NativeBootstrap.ps1).

& (Join-Path $PSScriptRoot 'Test-UnsignedArtifacts.ps1') `
    -Path $result.Path |
    Out-Null

Write-Output (
    "Validated native bootstrap '$($result.Path)' " +
    "($($result.Platform), version $($result.Version), " +
    "DependentLoadFlags=$($result.DependentLoadFlags), debug=$($result.IsDebug)).")
