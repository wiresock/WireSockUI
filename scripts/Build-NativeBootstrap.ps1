[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string] $Platform,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ExpectedPayloadListPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputPath,

    [switch] $DevelopmentBuild,
    [switch] $PackagingPayload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maximumManifestEntries = 4096
$maximumPayloadEntries = $maximumManifestEntries * 2
$maximumManifestBytes = 2MB
$maximumPayloadDepth = 32
$maximumPayloadFileBytes = 512MB
$maximumPayloadBytes = 2GB
$ignoredBuildMetadataExtensions = @(
    '.pdb',
    '.wixpdb',
    '.msi',
    '.msix',
    '.msp',
    '.cab',
    '.zip',
    '.nupkg',
    '.snupkg',
    '.sha256',
    '.obj',
    '.res',
    '.exp',
    '.lib',
    '.ilk',
    '.map'
)
$ignoredBuildMetadataSuffixes = @(
    '.spdx.json',
    '.sbom.json',
    '.intoto.jsonl'
)

Import-Module `
    -Name (Join-Path $PSScriptRoot 'NativeBootstrap.Validation.psm1') `
    -Force `
    -ErrorAction Stop

function Test-IsReservedDeviceSegment {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Segment
    )

    $dot = $Segment.IndexOf('.')
    $name = if ($dot -ge 0) {
        $Segment.Substring(0, $dot)
    }
    else {
        $Segment
    }

    if ($name -in @('CON', 'PRN', 'AUX', 'NUL')) {
        return $true
    }

    return $name.Length -eq 4 -and
        ($name.StartsWith('COM', [StringComparison]::OrdinalIgnoreCase) -or
         $name.StartsWith('LPT', [StringComparison]::OrdinalIgnoreCase)) -and
        $name[3] -ge '1' -and
        $name[3] -le '9'
}

function Test-IsIgnoredBuildMetadataPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $manifestPath = $RelativePath.Replace('\', '/')
    $segments = $manifestPath.Split('/')
    if ($segments.Count -gt 1 -and
        ($segments[0] -ieq 'publish' -or $segments[0] -ieq '_manifest')) {
        return $true
    }

    $name = $segments[$segments.Count - 1]
    if ($ignoredBuildMetadataExtensions -icontains
        [IO.Path]::GetExtension($name)) {
        return $true
    }
    foreach ($suffix in $ignoredBuildMetadataSuffixes) {
        if ($name.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    # The build creates this same-directory file only so MoveFileEx can publish
    # the fully validated launcher atomically. It is not part of the payload.
    return $segments.Count -eq 1 -and
        $name -cmatch '^\.WireSockUI\.[0-9a-f]{32}\.staging\.tmp$'
}

function Get-CanonicalRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $rootWithSeparator = $Root.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload file '$Path' is outside '$Root'."
    }

    $relativePath = $Path.Substring($rootWithSeparator.Length).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        $relativePath.StartsWith('/', [StringComparison]::Ordinal) -or
        $relativePath.Length -ge 32700 -or
        $relativePath.Contains("`t") -or
        $relativePath.Contains("`r") -or
        $relativePath.Contains("`n") -or
        $relativePath.Contains(':')) {
        throw "Payload file '$Path' does not have a canonical relative path."
    }

    $segments = $relativePath.Split('/')
    if ($segments.Count -gt $maximumPayloadDepth + 1) {
        throw "Payload file '$Path' exceeds the native host's directory-depth limit."
    }
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or
            $segment -eq '.' -or
            $segment -eq '..' -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            (Test-IsReservedDeviceSegment -Segment $segment)) {
            throw "Payload file '$Path' contains an unsafe path segment."
        }

        foreach ($character in $segment.ToCharArray()) {
            if ([int]$character -lt 32 -or
                $character -eq '"' -or
                $character -eq '<' -or
                $character -eq '>' -or
                $character -eq '|' -or
                $character -eq '*' -or
                $character -eq '?') {
                throw "Payload file '$Path' contains a character rejected by the native host."
            }
        }
    }

    return $relativePath
}

function Publish-ValidatedLauncher {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    if ($null -eq ('WireSockUI.Build.NativeFilePublisher' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WireSockUI.Build
{
    public static class NativeFilePublisher
    {
        private const uint MoveFileReplaceExisting = 0x00000001;
        private const uint MoveFileWriteThrough = 0x00000008;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            uint flags);

        public static void ReplaceAtomically(string source, string destination)
        {
            if (!MoveFileEx(
                    source,
                    destination,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to atomically publish the validated native launcher.");
        }
    }
}
'@
    }

    [WireSockUI.Build.NativeFilePublisher]::ReplaceAtomically($Source, $Destination)
}

function ConvertTo-RcLiteral {
    param([Parameter(Mandatory = $true)][string] $Value)
    return $Value.Replace('\', '\\').Replace('"', '\"')
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $algorithm.ComputeHash($stream)
        return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

$systemDirectory = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::System)
$trustedFsutilPath = Join-Path $systemDirectory 'fsutil.exe'
if ([string]::IsNullOrWhiteSpace($systemDirectory) -or
    -not (Test-Path -LiteralPath $trustedFsutilPath -PathType Leaf)) {
    throw 'The trusted Windows fsutil.exe path could not be resolved.'
}

function Assert-SingleLinkPayloadFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Payload file '$RelativePath' does not exist as a regular file."
    }
    $entry = Get-Item -LiteralPath $Path -Force
    if ($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Payload file '$RelativePath' is a reparse point."
    }

    $hardLinks = @(
        & $trustedFsutilPath hardlink list $entry.FullName 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect hard links for payload file '$RelativePath'."
    }
    if ($hardLinks.Count -ne 1) {
        throw "Payload file '$RelativePath' is hard-linked."
    }
    return $entry
}

function Assert-PayloadSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $LauncherPath,

        [Parameter(Mandatory = $true)]
        [object[]] $Entries
    )

    $expectedByPath =
        [Collections.Generic.Dictionary[string,object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Entries) {
        $expectedByPath.Add([string]$entry.Path, $entry)
    }

    $seenPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $relativePath = Get-CanonicalRelativePath `
            -Root $Root `
            -Path $file.FullName
        if (Test-IsIgnoredBuildMetadataPath -RelativePath $relativePath) {
            continue
        }
        if ([string]::Equals(
                $file.FullName,
                $LauncherPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            if ($relativePath -cne 'WireSockUI.exe') {
                throw "Payload contains a case-insensitive launcher-name collision at '$relativePath'."
            }
            continue
        }
        if (-not $seenPaths.Add($relativePath) -or
            -not $expectedByPath.ContainsKey($relativePath)) {
            throw "Payload inventory changed while building at '$relativePath'."
        }
    }
    if ($seenPaths.Count -ne $expectedByPath.Count) {
        throw 'Payload inventory changed while building the native launcher.'
    }

    foreach ($expected in $Entries) {
        $currentPath = Join-Path $Root (
            ([string]$expected.Path).Replace(
                '/',
                [IO.Path]::DirectorySeparatorChar))
        $current = Assert-SingleLinkPayloadFile `
            -Path $currentPath `
            -RelativePath ([string]$expected.Path)
        if ([Int64]$current.Length -ne [Int64]$expected.Size -or
            (Get-Sha256Hex -Path $current.FullName) -cne
                [string]$expected.Hash) {
            throw "Payload file '$($expected.Path)' changed while building the native launcher."
        }
    }

    foreach ($directory in
        Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force) {
        if ($directory.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Payload directory '$($directory.FullName)' became a reparse point while building."
        }
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repositoryRoot 'WireSockUI.Bootstrap'
$sourcePath = Join-Path $sourceDirectory 'bootstrap.cpp'
$manifestPath = Join-Path $sourceDirectory 'bootstrap.manifest'
$iconPath = Join-Path $repositoryRoot 'WireSockUI\Resources\wiresock.ico'

$resolvedPayloadDirectory = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::Equals(
        [IO.Path]::GetFileName($resolvedOutputPath),
        'WireSockUI.exe',
        [StringComparison]::Ordinal)) {
    throw 'The native launcher output filename must be exactly WireSockUI.exe.'
}
if (-not [string]::Equals(
        $resolvedPayloadDirectory,
        $outputDirectory.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The native launcher must be emitted directly into the payload directory it binds.'
}
if (-not (Test-Path -LiteralPath $resolvedPayloadDirectory -PathType Container)) {
    throw "Payload directory '$resolvedPayloadDirectory' does not exist."
}
if ((Get-Item -LiteralPath $resolvedPayloadDirectory -Force).Attributes.HasFlag(
        [IO.FileAttributes]::ReparsePoint)) {
    throw "Payload directory '$resolvedPayloadDirectory' is a reparse point."
}

$ignoredMetadataDirectories = @(
    Get-ChildItem -LiteralPath $resolvedPayloadDirectory -Force |
        Where-Object {
            $_.PSIsContainer -and
            ($_.Name -ieq 'publish' -or $_.Name -ieq '_manifest')
        })
if (($PackagingPayload -or -not $DevelopmentBuild) -and
    $ignoredMetadataDirectories.Count -ne 0) {
    throw "Production payload '$resolvedPayloadDirectory' contains a reserved publish or _manifest subtree. Build from a clean publish directory."
}

$versionCore = ($Version -split '[-+]', 2)[0]
$versionPattern = '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-+][0-9A-Za-z.-]+)?$'
if ($Version -notmatch $versionPattern) {
    throw "Version '$Version' must have three or four numeric components and an optional SemVer suffix."
}
$parsedVersion = $null
if (-not [Version]::TryParse($versionCore, [ref] $parsedVersion)) {
    throw "Version '$Version' is not a valid numeric product version."
}
$versionParts = @(
    $parsedVersion.Major,
    [Math]::Max(0, $parsedVersion.Minor),
    [Math]::Max(0, $parsedVersion.Build),
    [Math]::Max(0, $parsedVersion.Revision))
foreach ($part in $versionParts) {
    if ($part -gt [UInt16]::MaxValue) {
        throw "Version '$Version' contains a component larger than 65535."
    }
}
$numericVersion = $versionParts -join '.'
$resourceVersion = $versionParts -join ','

$resolvedExpectedPayloadListPath = [IO.Path]::GetFullPath($ExpectedPayloadListPath)
if (-not (Test-Path -LiteralPath $resolvedExpectedPayloadListPath -PathType Leaf)) {
    throw "Expected payload list '$resolvedExpectedPayloadListPath' does not exist."
}

foreach ($legacyName in @(
        'WireSockUI.Managed.exe',
        'WireSockUI.Managed.exe.config')) {
    $legacyPath = Join-Path $resolvedPayloadDirectory $legacyName
    if (Test-Path -LiteralPath $legacyPath) {
        throw "Payload contains obsolete directly executable managed artifact '$legacyName'. Build from a clean output directory."
    }
}

$runtimeEntryCount = 0
$hasLauncherEntry = $false
foreach ($entry in Get-ChildItem -LiteralPath $resolvedPayloadDirectory -Recurse -Force) {
    $relativeEntryPath = Get-CanonicalRelativePath `
        -Root $resolvedPayloadDirectory `
        -Path $entry.FullName
    if ($relativeEntryPath.StartsWith(
            'publish/', [StringComparison]::OrdinalIgnoreCase) -or
        $relativeEntryPath.StartsWith(
            '_manifest/', [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    if ($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Payload entry '$relativeEntryPath' is a reparse point."
    }

    ++$runtimeEntryCount
    if ($relativeEntryPath -ceq 'WireSockUI.exe') {
        $hasLauncherEntry = $true
    }
}
if (-not $hasLauncherEntry) {
    ++$runtimeEntryCount
}
if ($runtimeEntryCount -gt $maximumPayloadEntries) {
    throw "Payload contains $runtimeEntryCount runtime entries; the native host limit is $maximumPayloadEntries."
}

$payloadDirectories = @(
    Get-ChildItem -LiteralPath $resolvedPayloadDirectory -Recurse -Directory -Force)
foreach ($directory in $payloadDirectories) {
    if ($directory.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Payload directory '$($directory.FullName)' is a reparse point."
    }
    $relativeDirectory = Get-CanonicalRelativePath `
        -Root $resolvedPayloadDirectory `
        -Path $directory.FullName
    if ($relativeDirectory.Split('/').Count -gt $maximumPayloadDepth) {
        throw "Payload directory '$relativeDirectory' exceeds the native host's directory-depth limit."
    }
}

$expectedRelativePaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$payloadFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($listedPath in [IO.File]::ReadAllLines($resolvedExpectedPayloadListPath)) {
    if ([string]::IsNullOrWhiteSpace($listedPath)) {
        continue
    }

    $candidatePath = $listedPath.Trim().Replace('/', '\')
    if ([IO.Path]::IsPathRooted($candidatePath)) {
        $fullPath = [IO.Path]::GetFullPath($candidatePath)
    }
    else {
        $fullPath = [IO.Path]::GetFullPath(
            (Join-Path $resolvedPayloadDirectory $candidatePath))
    }
    $relativePath = Get-CanonicalRelativePath `
        -Root $resolvedPayloadDirectory `
        -Path $fullPath
    if ((Test-IsIgnoredBuildMetadataPath -RelativePath $relativePath) -or
        [string]::Equals(
            $fullPath, $resolvedOutputPath, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals(
            $relativePath,
            'WireSockUI.exe',
            [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    if (-not $expectedRelativePaths.Add($relativePath)) {
        continue
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Expected payload file '$relativePath' does not exist."
    }
    $payloadFiles.Add((Get-Item -LiteralPath $fullPath -Force))
}

$actualRuntimeFiles = @(
    Get-ChildItem -LiteralPath $resolvedPayloadDirectory -Recurse -File -Force)
$seenActualRuntimePaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($actualFile in $actualRuntimeFiles) {
    $relativePath = Get-CanonicalRelativePath `
        -Root $resolvedPayloadDirectory `
        -Path $actualFile.FullName
    if (Test-IsIgnoredBuildMetadataPath -RelativePath $relativePath) {
        continue
    }
    if (-not $seenActualRuntimePaths.Add($relativePath)) {
        throw "Payload contains a case-insensitive runtime-path collision at '$relativePath'."
    }
    if ([string]::Equals(
            $relativePath,
            'WireSockUI.exe',
            [StringComparison]::OrdinalIgnoreCase)) {
        if ($relativePath -cne 'WireSockUI.exe') {
            throw "Payload contains a case-insensitive launcher-name collision at '$relativePath'."
        }
        continue
    }
    if (-not $expectedRelativePaths.Contains($relativePath)) {
        throw "Unexpected runtime file '$relativePath' is not an MSBuild payload output."
    }
}

if ($payloadFiles.Count -eq 0 -or $payloadFiles.Count -gt $maximumManifestEntries) {
    throw "The native launcher payload must contain between 1 and $maximumManifestEntries runtime files."
}

$entries = [Collections.Generic.List[object]]::new()
$seenPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
[Int64] $totalBytes = 0
foreach ($file in $payloadFiles) {
    $relativePath = Get-CanonicalRelativePath `
        -Root $resolvedPayloadDirectory `
        -Path $file.FullName
    if (-not $seenPaths.Add($relativePath)) {
        throw "Payload contains a case-insensitive path collision at '$relativePath'."
    }
    if ($file.Length -lt 0 -or $file.Length -gt $maximumPayloadFileBytes) {
        throw "Payload file '$relativePath' exceeds the $maximumPayloadFileBytes-byte limit."
    }
    $file = Assert-SingleLinkPayloadFile `
        -Path $file.FullName `
        -RelativePath $relativePath

    if ($totalBytes -gt $maximumPayloadBytes - [Int64] $file.Length) {
        throw "The runtime payload exceeds the $maximumPayloadBytes-byte limit."
    }
    $totalBytes += [Int64] $file.Length
    if ($totalBytes -gt $maximumPayloadBytes) {
        throw "The runtime payload exceeds the $maximumPayloadBytes-byte limit."
    }

    $hash = Get-Sha256Hex -Path $file.FullName
    $entries.Add([pscustomobject]@{
        Path = $relativePath
        Size = [Int64] $file.Length
        Hash = $hash
    })
}

$entries.Sort([Comparison[object]] {
    param($left, $right)
    return [StringComparer]::OrdinalIgnoreCase.Compare(
        [string]$left.Path,
        [string]$right.Path)
})
if (-not ($entries.Path -ccontains 'WireSockUI.Managed.dll')) {
    throw "Payload '$resolvedPayloadDirectory' does not contain WireSockUI.Managed.dll."
}
if (-not ($entries.Path -ccontains 'WireSockUI.exe.config')) {
    throw "Payload '$resolvedPayloadDirectory' does not contain WireSockUI.exe.config."
}
$managedAssemblyPath = Join-Path $resolvedPayloadDirectory 'WireSockUI.Managed.dll'
Assert-ManagedAssemblyPlatform `
    -Path $managedAssemblyPath `
    -ExpectedPlatform $Platform | Out-Null

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    'WireSockUI-bootstrap-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
$stagedLauncherPath = $null

try {
    $payloadManifestPath = Join-Path $temporaryDirectory 'payload.manifest'
    $manifestLines = [Collections.Generic.List[string]]::new()
    $manifestLines.Add('WireSockUI-Payload-v1')
    foreach ($entry in $entries) {
        $manifestLines.Add("$($entry.Hash)`t$($entry.Size)`t$($entry.Path)")
    }
    $manifestText = ($manifestLines -join "`n") + "`n"
    $manifestEncoding = [Text.UTF8Encoding]::new($false, $true)
    $manifestBytes = $manifestEncoding.GetBytes($manifestText)
    if ($manifestBytes.Length -gt $maximumManifestBytes) {
        throw "The native payload manifest exceeds the $maximumManifestBytes-byte runtime limit."
    }
    [IO.File]::WriteAllBytes($payloadManifestPath, $manifestBytes)

    $resourceScriptPath = Join-Path $temporaryDirectory 'bootstrap.rc'
    $resourceScript = @"
#include <windows.h>

#define IDR_PAYLOAD_MANIFEST 201
IDI_APP_ICON ICON "$(ConvertTo-RcLiteral $iconPath)"
IDR_PAYLOAD_MANIFEST RCDATA "$(ConvertTo-RcLiteral $payloadManifestPath)"

VS_VERSION_INFO VERSIONINFO
 FILEVERSION $resourceVersion
 PRODUCTVERSION $resourceVersion
 FILEFLAGSMASK VS_FFI_FILEFLAGSMASK
#ifdef _DEBUG
 FILEFLAGS VS_FF_DEBUG
#else
 FILEFLAGS 0
#endif
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_APP
 FILESUBTYPE 0
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904B0"
        BEGIN
            VALUE "CompanyName", "WireSockUI\0"
            VALUE "FileDescription", "WireSock UI secure launcher\0"
            VALUE "FileVersion", "$numericVersion\0"
            VALUE "InternalName", "WireSockUI\0"
            VALUE "OriginalFilename", "WireSockUI.exe\0"
            VALUE "ProductName", "WireSock UI\0"
            VALUE "ProductVersion", "$numericVersion\0"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x0409, 1200
    END
END
"@
    [IO.File]::WriteAllText(
        $resourceScriptPath,
        $resourceScript,
        [Text.UTF8Encoding]::new($true))

    $vswherePath = Join-Path ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw 'Visual Studio Installer vswhere.exe is required to build the native WireSock UI launcher.'
    }

    $visualStudioPath = (& $vswherePath -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($visualStudioPath)) {
        throw 'Visual C++ build tools are required to build the native WireSock UI launcher.'
    }

    $developerCommand = Join-Path $visualStudioPath 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) {
        throw "Visual Studio developer command file '$developerCommand' was not found."
    }

    $architecture = switch ($Platform) {
        'x86' { 'x86' }
        'x64' { 'amd64' }
        'ARM64' { 'arm64' }
    }

    $environmentCommand =
        'call "' + $developerCommand + '" -no_logo -arch=' + $architecture +
        ' -host_arch=amd64 >nul && set'
    $developerEnvironment = & $env:ComSpec /d /c $environmentCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Visual Studio developer environment initialization failed with exit code $LASTEXITCODE."
    }

    $developerPath = $null
    foreach ($entry in $developerEnvironment) {
        $separator = $entry.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $name = $entry.Substring(0, $separator)
        $value = $entry.Substring($separator + 1)
        if ([string]::Equals($name, 'Path', [StringComparison]::OrdinalIgnoreCase)) {
            if ($null -eq $developerPath -or $value.Length -gt $developerPath.Length) {
                $developerPath = $value
            }
            continue
        }

        [Environment]::SetEnvironmentVariable(
            $name,
            $value,
            [EnvironmentVariableTarget]::Process)
    }
    if (-not [string]::IsNullOrWhiteSpace($developerPath)) {
        [Environment]::SetEnvironmentVariable(
            'Path',
            $developerPath,
            [EnvironmentVariableTarget]::Process)
    }

    $objectPath = Join-Path $temporaryDirectory "WireSockUI.Bootstrap.$Platform.obj"
    $resourceOutputPath = Join-Path $temporaryDirectory "WireSockUI.Bootstrap.$Platform.res"
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $stagedLauncherPath = Join-Path $outputDirectory (
        '.WireSockUI.' + [Guid]::NewGuid().ToString('N') + '.staging.tmp')

    $resourceArguments = @('/nologo', "/fo$resourceOutputPath")
    if ($DevelopmentBuild) {
        $resourceArguments += '/d'
        $resourceArguments += '_DEBUG'
    }
    $resourceArguments += $resourceScriptPath
    & rc.exe @resourceArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native WireSock UI resource compilation failed with exit code $LASTEXITCODE."
    }

    $compilerArguments = @(
        '/nologo', '/c', '/std:c++17', '/permissive-', '/O2', '/MT', '/EHsc',
        '/GS', '/sdl', '/guard:cf', '/W4', '/WX', '/DUNICODE', '/D_UNICODE',
        "/Fo$objectPath")
    if ($DevelopmentBuild) {
        $compilerArguments += '/DWIRESOCKUI_DEVELOPMENT_BUILD=1'
    }
    $compilerArguments += $sourcePath

    & cl.exe @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native WireSock UI compilation failed with exit code $LASTEXITCODE."
    }

    $linkerArguments = @(
        '/nologo',
        "/OUT:$stagedLauncherPath",
        '/SUBSYSTEM:WINDOWS',
        '/DYNAMICBASE',
        '/NXCOMPAT',
        '/GUARD:CF',
        '/DEPENDENTLOADFLAG:0x800',
        '/DELAYLOAD:bcrypt.dll',
        '/MANIFEST:EMBED',
        '/MANIFESTUAC:NO',
        "/MANIFESTINPUT:$manifestPath",
        $objectPath,
        $resourceOutputPath,
        'advapi32.lib',
        'bcrypt.lib',
        'delayimp.lib',
        'ole32.lib',
        'user32.lib')
    # No /CETCOMPAT: the launcher hosts the .NET Framework CLR, which is not
    # compatible with CET shadow stacks. On CET-capable CPUs every managed
    # exception (even a caught one) then fails fast with 0xC0000409 /
    # FAST_FAIL_SET_CONTEXT_DENIED while the CLR unwinds into JIT code.
    if ($Platform -ne 'x86') {
        $linkerArguments += '/HIGHENTROPYVA'
    }
    & link.exe @linkerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native WireSock UI linking failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $stagedLauncherPath -PathType Leaf)) {
        throw "Native WireSock UI staging launcher '$stagedLauncherPath' was not produced."
    }

    Assert-NativeBootstrap `
        -Path $stagedLauncherPath `
        -ExpectedPlatform $Platform `
        -ExpectedVersion $numericVersion `
        -RequireProductionBuild:(-not $DevelopmentBuild) | Out-Null

    Assert-PayloadSnapshot `
        -Root $resolvedPayloadDirectory `
        -LauncherPath $resolvedOutputPath `
        -Entries $entries.ToArray()

    Publish-ValidatedLauncher `
        -Source $stagedLauncherPath `
        -Destination $resolvedOutputPath
    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        throw "Validated native WireSock UI launcher '$resolvedOutputPath' was not atomically published."
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($stagedLauncherPath) -and
        (Test-Path -LiteralPath $stagedLauncherPath)) {
        Remove-Item -LiteralPath $stagedLauncherPath -Force
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporaryDirectory =
            [IO.Path]::GetFullPath($temporaryDirectory)
        $normalizedTempRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\', '/') + '\'
        $temporaryDirectoryEntry =
            Get-Item -LiteralPath $resolvedTemporaryDirectory -Force
        if (-not $resolvedTemporaryDirectory.StartsWith(
                $normalizedTempRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $resolvedTemporaryDirectory).StartsWith(
                'WireSockUI-bootstrap-',
                [StringComparison]::Ordinal) -or
            -not $temporaryDirectoryEntry.PSIsContainer -or
            ($temporaryDirectoryEntry.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively clean unsafe native-bootstrap staging path '$resolvedTemporaryDirectory'."
        }
        [IO.Directory]::Delete($resolvedTemporaryDirectory, $true)
    }
}
