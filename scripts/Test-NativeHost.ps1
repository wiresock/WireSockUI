[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $LauncherPath,

    [switch] $SkipBcryptSentinel
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$nativeHostProcessesSafeForCleanup = $true

$resolvedLauncher = [IO.Path]::GetFullPath($LauncherPath)
if (-not (Test-Path -LiteralPath $resolvedLauncher -PathType Leaf)) {
    throw "Native host '$resolvedLauncher' does not exist."
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The native-host smoke test requires Windows.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The native-host smoke test requires an already-elevated disposable Windows runner.'
}
$administratorsSid = New-Object Security.Principal.SecurityIdentifier(
    'S-1-5-32-544')
$systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$isDebugLauncher =
    [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedLauncher).IsDebug
if (-not $SkipBcryptSentinel -and -not $isDebugLauncher) {
    throw 'The bcrypt sentinel rebuild requires a Debug launcher. Use -SkipBcryptSentinel for an installed production launcher.'
}

function Get-PortableExecutablePlatform {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $reader = New-Object IO.BinaryReader $stream
        try {
            if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) {
                throw "Portable executable '$Path' has no DOS header."
            }
            $stream.Position = 0x3c
            [UInt32]$peOffset = $reader.ReadUInt32()
            if ($peOffset -gt $stream.Length - 6) {
                throw "Portable executable '$Path' has an invalid PE offset."
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "Portable executable '$Path' has no PE signature."
            }
            switch ($reader.ReadUInt16()) {
                0x014c { return 'x86' }
                0x8664 { return 'x64' }
                0xaa64 { return 'ARM64' }
                default { throw "Portable executable '$Path' has an unsupported machine type." }
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Set-PrivateTestPathAcl {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileSystemInfo] $Entry
    )

    if (($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Native-host test entry '$($Entry.FullName)' is a reparse point."
    }
    $security = if ($Entry.PSIsContainer) {
        New-Object Security.AccessControl.DirectorySecurity
    }
    else {
        New-Object Security.AccessControl.FileSecurity
    }
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = if ($Entry.PSIsContainer) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else {
        [Security.AccessControl.InheritanceFlags]::None
    }
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Entry.FullName -AclObject $security
}

function Set-PrivateTestTreeAcl {
    param([Parameter(Mandatory = $true)][string] $Root)

    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $rootEntry = Get-Item -LiteralPath $resolvedRoot -Force
    if (-not $rootEntry.PSIsContainer) {
        throw "Native-host test root '$resolvedRoot' is not a directory."
    }

    Set-PrivateTestPathAcl -Entry $rootEntry
    foreach ($entry in Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force) {
        Set-PrivateTestPathAcl -Entry $entry
    }
}

function Initialize-VisualCppEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('x86', 'x64', 'ARM64')]
        [string] $Platform
    )

    $vswherePath = Join-Path ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw 'Visual Studio Installer vswhere.exe is required for the bcrypt sentinel test.'
    }
    $visualStudioPath = (& $vswherePath -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($visualStudioPath)) {
        throw 'Visual C++ build tools are required for the bcrypt sentinel test.'
    }
    $developerCommand = Join-Path $visualStudioPath 'Common7\Tools\VsDevCmd.bat'
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
        throw "Visual C++ environment initialization failed with exit code $LASTEXITCODE."
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
}

function Invoke-NativeHostProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [string] $BcryptSentinelMarker,

        [switch] $ExpectValidationFailure,

        [switch] $ExpectManagedBoundaryFailure
    )

    if ($ExpectValidationFailure -and $ExpectManagedBoundaryFailure) {
        throw 'A native-host probe cannot expect two different failure contracts.'
    }

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Path
    $startInfo.Arguments = '--native-host-self-test "argument with spaces"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['COR_ENABLE_PROFILING'] = '1'
    $startInfo.EnvironmentVariables['COR_PROFILER'] =
        '{11111111-1111-1111-1111-111111111111}'
    $startInfo.EnvironmentVariables['COR_PROFILER_PATH'] =
        'C:\nonexistent\untrusted-profiler.dll'
    $startInfo.EnvironmentVariables['COR_TEST_SENTINEL'] = 'must-be-removed'
    $startInfo.EnvironmentVariables['CORECLR_TEST_SENTINEL'] = 'must-be-removed'
    $startInfo.EnvironmentVariables['COMPLUS_TEST_SENTINEL'] = 'must-be-removed'
    $startInfo.EnvironmentVariables['COMPLUS_Version'] = 'v2.0.50727'
    $startInfo.EnvironmentVariables['DOTNET_TEST_SENTINEL'] = 'must-be-removed'
    $startInfo.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] =
        'C:\nonexistent\startup-hook.dll'
    $startInfo.EnvironmentVariables['CORPATH'] = 'C:\nonexistent\corpath'
    $startInfo.EnvironmentVariables['APPDOMAIN_MANAGER_ASM'] = 'Untrusted.Manager'
    $startInfo.EnvironmentVariables['APPDOMAIN_MANAGER_TYPE'] = 'Untrusted.Manager.Type'
    $startInfo.EnvironmentVariables['DEVPATH'] = 'C:\nonexistent\devpath'
    $startInfo.EnvironmentVariables['WIRESOCKUI_DEVELOPMENT_SELF_TEST_NO_UI'] = '1'
    $startInfo.EnvironmentVariables['WIRESOCKUI_NATIVE_SELF_TEST_DIAGNOSTICS'] = '1'
    if ($ExpectManagedBoundaryFailure) {
        $startInfo.EnvironmentVariables[
            'WIRESOCKUI_DEVELOPMENT_MANAGED_BOUNDARY_FAILURE'] = '1'
    }
    if (-not [string]::IsNullOrWhiteSpace($BcryptSentinelMarker)) {
        $startInfo.EnvironmentVariables['WIRESOCKUI_BCRYPT_SENTINEL'] =
            $BcryptSentinelMarker
    }

    try {
        $process = [Diagnostics.Process]::Start($startInfo)
    }
    catch [System.ComponentModel.Win32Exception] {
        if ($_.Exception.NativeErrorCode -eq 740) {
            throw 'The native-host smoke test requires an already-elevated disposable Windows runner.'
        }
        throw
    }
    if ($null -eq $process) {
        throw 'The native-host smoke process could not be created.'
    }
    $standardErrorTask = $process.StandardError.ReadToEndAsync()

    try {
        if (-not $process.WaitForExit(30000)) {
            try {
                $process.Kill($true)
            }
            catch {
                try {
                    $process.Kill()
                }
                catch {
                    # The bounded post-kill wait below determines whether
                    # filesystem cleanup is safe.
                }
            }
            if (-not $process.WaitForExit(30000)) {
                $script:nativeHostProcessesSafeForCleanup = $false
                throw 'The native-host smoke process timed out and could not be terminated; preserving its test directory.'
            }
            $standardError = $standardErrorTask.GetAwaiter().GetResult().Trim()
            $detail = if ([string]::IsNullOrWhiteSpace($standardError)) {
                ''
            }
            else {
                " Diagnostic: $standardError"
            }
            throw "The native-host smoke process did not exit within 30 seconds.$detail"
        }
        $standardError = $standardErrorTask.GetAwaiter().GetResult().Trim()
        if ($ExpectValidationFailure) {
            if ($process.ExitCode -ne 40 -or
                -not $standardError.StartsWith(
                    'WireSockUI native self-test: ',
                    [StringComparison]::Ordinal)) {
                $detail = if ([string]::IsNullOrWhiteSpace($standardError)) {
                    ''
                }
                else {
                    " Diagnostic: $standardError"
                }
                throw (
                    'The deliberately invalid payload did not produce the ' +
                    "native pre-CLR validation contract (exit 40). Actual exit " +
                    "$($process.ExitCode).$detail")
            }
            return
        }
        if ($ExpectManagedBoundaryFailure) {
            $managedBoundaryDiagnostic =
                'WireSockUI native self-test: phase: managed entry point returned'
            if ($process.ExitCode -ne 31 -or
                -not $standardError.Contains($managedBoundaryDiagnostic)) {
                $detail = if ([string]::IsNullOrWhiteSpace($standardError)) {
                    ''
                }
                else {
                    " Diagnostic: $standardError"
                }
                throw (
                    'The injected managed exception did not produce the ' +
                    "managed boundary contract (exit 31). Actual exit " +
                    "$($process.ExitCode).$detail")
            }
            if ($standardError.Contains(
                    'The validated WireSock UI managed entry point failed:')) {
                throw (
                    'The injected managed exception escaped through ' +
                    'ExecuteInDefaultAppDomain as an HRESULT.')
            }
            return
        }
        if ($process.ExitCode -ne 0) {
            $detail = if ([string]::IsNullOrWhiteSpace($standardError)) {
                ''
            }
            else {
                " Diagnostic: $standardError"
            }
            throw "The native-host smoke process returned diagnostic exit code $($process.ExitCode).$detail"
        }
    }
    finally {
        $process.Dispose()
    }
}

if ($SkipBcryptSentinel) {
    # Installed launchers live under an administrator-owned directory and can
    # be probed in place. Build outputs intentionally remain user-writable, so
    # Debug validation uses the private staged payload created below.
    Invoke-NativeHostProbe -Path $resolvedLauncher
}

if (-not $SkipBcryptSentinel) {
    if ($null -eq ('WireSockUI.NativeHostTests.NativeMethods' -as [type])) {
        Add-Type -Namespace WireSockUI.NativeHostTests -Name NativeMethods `
            -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                SetLastError = true)]
            public static extern bool GetVolumePathNameW(
                string fileName,
                System.Text.StringBuilder volumePathName,
                int bufferLength);

            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            public static extern uint GetDriveTypeW(string rootPathName);
'@
    }

    $platform = Get-PortableExecutablePlatform -Path $resolvedLauncher
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $buildBootstrap = Join-Path $PSScriptRoot 'Build-NativeBootstrap.ps1'
    $sourceDirectory = Split-Path -Parent $resolvedLauncher
    $testBaseDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles)
    if ([string]::IsNullOrWhiteSpace($testBaseDirectory) -or
        -not [IO.Path]::IsPathRooted($testBaseDirectory)) {
        throw 'The native-host smoke test could not resolve Program Files.'
    }
    $testBaseDirectory = [IO.Path]::GetFullPath($testBaseDirectory)
    if (-not [IO.Directory]::Exists($testBaseDirectory)) {
        throw 'The native-host smoke test requires an existing Program Files directory.'
    }
    $testBaseEntry = Get-Item -LiteralPath $testBaseDirectory -Force
    if (-not $testBaseEntry.PSIsContainer -or
        ($testBaseEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The native-host smoke test requires an ordinary Program Files directory.'
    }
    $volumePath = [Text.StringBuilder]::new(32768)
    if (-not [WireSockUI.NativeHostTests.NativeMethods]::GetVolumePathNameW(
            $testBaseDirectory,
            $volumePath,
            $volumePath.Capacity) -or
        [WireSockUI.NativeHostTests.NativeMethods]::GetDriveTypeW(
            $volumePath.ToString()) -ne 3) {
        throw 'The native-host smoke test requires Program Files on a local fixed drive.'
    }
    $testRoot = Join-Path $testBaseDirectory (
        'WireSockUI.NativeHost.' + [Guid]::NewGuid().ToString('N'))
    $payloadDirectory = Join-Path $testRoot 'payload'
    $buildDirectory = Join-Path $testRoot 'build'
    try {
        [void][IO.Directory]::CreateDirectory($testRoot)
        Set-PrivateTestTreeAcl -Root $testRoot
        [void][IO.Directory]::CreateDirectory($payloadDirectory)
        [void][IO.Directory]::CreateDirectory($buildDirectory)

        foreach ($entry in Get-ChildItem -LiteralPath $sourceDirectory -Force) {
            if ($entry.Name -ieq 'publish' -or
                $entry.Name -ieq '_manifest' -or
                $entry.Name -ceq 'WireSockUI.exe' -or
                $entry.Name.EndsWith('.staging.tmp', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            Copy-Item `
                -LiteralPath $entry.FullName `
                -Destination $payloadDirectory `
                -Recurse
        }

        # Exercise the exact launcher produced by the project after moving the
        # complete payload under a trusted Debug-only ACL. This preserves build
        # integration coverage without weakening the application's path policy.
        $testLauncherPath = Join-Path $payloadDirectory 'WireSockUI.exe'
        Copy-Item `
            -LiteralPath $resolvedLauncher `
            -Destination $testLauncherPath
        Set-PrivateTestTreeAcl -Root $testRoot
        Invoke-NativeHostProbe -Path $testLauncherPath
        Invoke-NativeHostProbe `
            -Path $testLauncherPath `
            -ExpectManagedBoundaryFailure

        Initialize-VisualCppEnvironment -Platform $platform
        $sentinelSourcePath = Join-Path $buildDirectory 'bcrypt-sentinel.cpp'
        $sentinelDefinitionPath = Join-Path $buildDirectory 'bcrypt-sentinel.def'
        $sentinelObjectPath = Join-Path $buildDirectory 'bcrypt-sentinel.obj'
        $sentinelLibraryPath = Join-Path $buildDirectory 'bcrypt-sentinel.lib'
        $sentinelDllPath = Join-Path $payloadDirectory 'bcrypt.dll'
        $sentinelSource = @'
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace
{
    constexpr wchar_t MarkerVariable[] = L"WIRESOCKUI_BCRYPT_SENTINEL";
    constexpr LONG NotImplemented = static_cast<LONG>(0xC0000002UL);
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH)
        return TRUE;

    wchar_t marker[32768] = {};
    constexpr DWORD markerCapacity =
        static_cast<DWORD>(sizeof(marker) / sizeof(marker[0]));
    const DWORD length = GetEnvironmentVariableW(
        MarkerVariable, marker, markerCapacity);
    if (length == 0 || length >= markerCapacity)
        return TRUE;

    const HANDLE file = CreateFileW(
        marker,
        GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
        nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        constexpr char content[] = "app-local bcrypt.dll loaded before wWinMain";
        DWORD written = 0;
        WriteFile(
            file,
            content,
            static_cast<DWORD>(sizeof(content) - 1),
            &written,
            nullptr);
        CloseHandle(file);
    }
    return TRUE;
}

extern "C" LONG WINAPI SentinelOpen(void*, const wchar_t*, const wchar_t*, ULONG)
{
    return NotImplemented;
}
extern "C" LONG WINAPI SentinelClose(void*, ULONG)
{
    return NotImplemented;
}
extern "C" LONG WINAPI SentinelGetProperty(
    void*, const wchar_t*, unsigned char*, ULONG, ULONG*, ULONG)
{
    return NotImplemented;
}
extern "C" LONG WINAPI SentinelCreateHash(
    void*, void**, unsigned char*, ULONG, unsigned char*, ULONG, ULONG)
{
    return NotImplemented;
}
extern "C" LONG WINAPI SentinelDestroyHash(void*)
{
    return NotImplemented;
}
extern "C" LONG WINAPI SentinelHashData(void*, unsigned char*, ULONG, ULONG)
{
    return NotImplemented;
}
extern "C" LONG WINAPI SentinelFinishHash(void*, unsigned char*, ULONG, ULONG)
{
    return NotImplemented;
}
'@
        [IO.File]::WriteAllText(
            $sentinelSourcePath,
            $sentinelSource,
            [Text.UTF8Encoding]::new($false))

        $aliases = if ($platform -eq 'x86') {
            @(
                ' BCryptOpenAlgorithmProvider=_SentinelOpen@16',
                ' BCryptCloseAlgorithmProvider=_SentinelClose@8',
                ' BCryptGetProperty=_SentinelGetProperty@24',
                ' BCryptCreateHash=_SentinelCreateHash@28',
                ' BCryptDestroyHash=_SentinelDestroyHash@4',
                ' BCryptHashData=_SentinelHashData@16',
                ' BCryptFinishHash=_SentinelFinishHash@16'
            )
        }
        else {
            @(
                ' BCryptOpenAlgorithmProvider=SentinelOpen',
                ' BCryptCloseAlgorithmProvider=SentinelClose',
                ' BCryptGetProperty=SentinelGetProperty',
                ' BCryptCreateHash=SentinelCreateHash',
                ' BCryptDestroyHash=SentinelDestroyHash',
                ' BCryptHashData=SentinelHashData',
                ' BCryptFinishHash=SentinelFinishHash'
            )
        }
        [IO.File]::WriteAllLines(
            $sentinelDefinitionPath,
            @('LIBRARY bcrypt', 'EXPORTS') + $aliases,
            [Text.UTF8Encoding]::new($false))

        & cl.exe /nologo /c /std:c++17 /O2 /MT /GS /sdl /DUNICODE /D_UNICODE `
            "/Fo$sentinelObjectPath" $sentinelSourcePath
        if ($LASTEXITCODE -ne 0) {
            throw "bcrypt sentinel compilation failed with exit code $LASTEXITCODE."
        }
        & link.exe /nologo /DLL `
            "/OUT:$sentinelDllPath" `
            "/DEF:$sentinelDefinitionPath" `
            "/IMPLIB:$sentinelLibraryPath" `
            $sentinelObjectPath kernel32.lib
        if ($LASTEXITCODE -ne 0 -or
            -not (Test-Path -LiteralPath $sentinelDllPath -PathType Leaf)) {
            throw "bcrypt sentinel linking failed with exit code $LASTEXITCODE."
        }

        $expectedPayloadListPath = Join-Path $buildDirectory 'expected-payload.txt'
        $expectedPayload = @(
            Get-ChildItem -LiteralPath $payloadDirectory -Recurse -File |
                ForEach-Object {
                    $_.FullName.Substring(
                        $payloadDirectory.TrimEnd('\', '/').Length).
                        TrimStart('\', '/')
                }
        )
        [IO.File]::WriteAllLines(
            $expectedPayloadListPath,
            $expectedPayload,
            [Text.UTF8Encoding]::new($false))

        $launcherVersion =
            [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedLauncher).ProductVersion
        & $buildBootstrap `
            -Platform $platform `
            -Version $launcherVersion `
            -PayloadDirectory $payloadDirectory `
            -ExpectedPayloadListPath $expectedPayloadListPath `
            -OutputPath $testLauncherPath `
            -DevelopmentBuild
        if ($LASTEXITCODE -ne 0) {
            throw "Sentinel-bound native launcher build failed with exit code $LASTEXITCODE."
        }

        Set-PrivateTestTreeAcl -Root $testRoot
        $sentinelMarker = Join-Path $buildDirectory 'bcrypt-loaded.txt'
        Invoke-NativeHostProbe `
            -Path $testLauncherPath `
            -BcryptSentinelMarker $sentinelMarker
        if (Test-Path -LiteralPath $sentinelMarker) {
            throw 'The native loader executed an app-local bcrypt.dll before wWinMain.'
        }

        $managedPayloadPath = Join-Path $payloadDirectory 'WireSockUI.Managed.dll'
        $managedPayloadBytes = [IO.File]::ReadAllBytes($managedPayloadPath)
        try {
            $managedPayloadBytes[$managedPayloadBytes.Length - 1] =
                $managedPayloadBytes[$managedPayloadBytes.Length - 1] -bxor 0xff
            [IO.File]::WriteAllBytes($managedPayloadPath, $managedPayloadBytes)
            Invoke-NativeHostProbe `
                -Path $testLauncherPath `
                -ExpectValidationFailure
        }
        finally {
            $managedPayloadBytes[$managedPayloadBytes.Length - 1] =
                $managedPayloadBytes[$managedPayloadBytes.Length - 1] -bxor 0xff
            [IO.File]::WriteAllBytes($managedPayloadPath, $managedPayloadBytes)
        }

        $unexpectedPayloadPath = Join-Path $payloadDirectory 'unexpected.dll'
        try {
            [IO.File]::WriteAllBytes($unexpectedPayloadPath, [byte[]](1, 2, 3, 4))
            Invoke-NativeHostProbe `
                -Path $testLauncherPath `
                -ExpectValidationFailure
        }
        finally {
            if ([IO.File]::Exists($unexpectedPayloadPath)) {
                Remove-Item -LiteralPath $unexpectedPayloadPath -Force
            }
        }

        # A .local sidecar changes Windows DLL/COM redirection behavior even
        # though it is not itself a DLL. The embedded payload manifest must
        # therefore bind every runtime file, not only executable extensions.
        $dotLocalPath = Join-Path $payloadDirectory 'WireSockUI.exe.local'
        try {
            [IO.File]::WriteAllBytes($dotLocalPath, [byte[]]::new(0))
            Invoke-NativeHostProbe `
                -Path $testLauncherPath `
                -ExpectValidationFailure
        }
        finally {
            if ([IO.File]::Exists($dotLocalPath)) {
                Remove-Item -LiteralPath $dotLocalPath -Force
            }
        }

        $hardLinkBackingPath = Join-Path $buildDirectory 'managed-hardlink-backing.dll'
        [IO.File]::WriteAllBytes($hardLinkBackingPath, $managedPayloadBytes)
        try {
            Remove-Item -LiteralPath $managedPayloadPath -Force
            New-Item `
                -ItemType HardLink `
                -Path $managedPayloadPath `
                -Target $hardLinkBackingPath |
                Out-Null
            Invoke-NativeHostProbe `
                -Path $testLauncherPath `
                -ExpectValidationFailure
        }
        finally {
            if ([IO.File]::Exists($managedPayloadPath)) {
                Remove-Item -LiteralPath $managedPayloadPath -Force
            }
            [IO.File]::WriteAllBytes($managedPayloadPath, $managedPayloadBytes)
        }

        $payloadSubdirectory = Get-ChildItem `
            -LiteralPath $payloadDirectory `
            -Directory `
            -Force |
            Select-Object -First 1
        if ($null -ne $payloadSubdirectory) {
            $originalSubdirectoryPath = $payloadSubdirectory.FullName
            $reparseTargetPath = Join-Path $buildDirectory 'reparse-target'
            Move-Item `
                -LiteralPath $originalSubdirectoryPath `
                -Destination $reparseTargetPath
            try {
                New-Item `
                    -ItemType Junction `
                    -Path $originalSubdirectoryPath `
                    -Target $reparseTargetPath |
                    Out-Null
                Invoke-NativeHostProbe `
                    -Path $testLauncherPath `
                    -ExpectValidationFailure
            }
            finally {
                if ([IO.Directory]::Exists($originalSubdirectoryPath)) {
                    Remove-Item -LiteralPath $originalSubdirectoryPath -Force
                }
                Move-Item `
                    -LiteralPath $reparseTargetPath `
                    -Destination $originalSubdirectoryPath
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) {
            if (-not $nativeHostProcessesSafeForCleanup) {
                Write-Warning (
                    "Preserving native-host test directory '$testRoot' because " +
                    'a timed-out process may still be using it.')
            }
            else {
                $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
                $normalizedTestBase = [IO.Path]::GetFullPath(
                    $testBaseDirectory).TrimEnd('\', '/') + '\'
                $testRootEntry = Get-Item -LiteralPath $resolvedTestRoot -Force
                if (-not $resolvedTestRoot.StartsWith(
                        $normalizedTestBase,
                        [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Split-Path -Leaf $resolvedTestRoot).StartsWith(
                        'WireSockUI.NativeHost.',
                        [StringComparison]::Ordinal) -or
                    -not $testRootEntry.PSIsContainer -or
                    ($testRootEntry.Attributes -band
                        [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing to recursively clean unsafe native-host test path '$resolvedTestRoot'."
                }
                [IO.Directory]::Delete($resolvedTestRoot, $true)
            }
        }
    }
}

Write-Output "Validated the in-process CLR host and System32-only dependent DLL policy at '$resolvedLauncher'."
