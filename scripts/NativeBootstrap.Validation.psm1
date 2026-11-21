Set-StrictMode -Version Latest

function Read-UInt16At {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [Int64] $Offset
    )

    if ($Offset -lt 0 -or $Offset -gt $Reader.BaseStream.Length - 2) {
        throw "Portable executable offset $Offset is outside the file."
    }
    $Reader.BaseStream.Position = $Offset
    return $Reader.ReadUInt16()
}

function Read-UInt32At {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [Int64] $Offset
    )

    if ($Offset -lt 0 -or $Offset -gt $Reader.BaseStream.Length - 4) {
        throw "Portable executable offset $Offset is outside the file."
    }
    $Reader.BaseStream.Position = $Offset
    return $Reader.ReadUInt32()
}

function Read-UInt64At {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [Int64] $Offset
    )

    if ($Offset -lt 0 -or $Offset -gt $Reader.BaseStream.Length - 8) {
        throw "Portable executable offset $Offset is outside the file."
    }
    $Reader.BaseStream.Position = $Offset
    return $Reader.ReadUInt64()
}

function Convert-RvaToFileOffset {
    param(
        [Parameter(Mandatory = $true)]
        [UInt32] $Rva,

        [Parameter(Mandatory = $true)]
        [UInt32] $RequiredBytes,

        [Parameter(Mandatory = $true)]
        [object[]] $Sections,

        [Parameter(Mandatory = $true)]
        [Int64] $FileLength,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    foreach ($section in $Sections) {
        [UInt64]$sectionSpan = if ([UInt64]$section.VirtualSize -eq 0) {
            [UInt64]$section.RawSize
        }
        else {
            [UInt64]$section.VirtualSize
        }
        if ([UInt64]$Rva -lt [UInt64]$section.VirtualAddress -or
            [UInt64]$Rva -ge [UInt64]$section.VirtualAddress + $sectionSpan) {
            continue
        }

        [UInt64]$delta = [UInt64]$Rva - [UInt64]$section.VirtualAddress
        if ($delta + [UInt64]$RequiredBytes -gt $sectionSpan -or
            $delta + [UInt64]$RequiredBytes -gt [UInt64]$section.RawSize) {
            throw "$Label is virtual-only or truncated."
        }
        [UInt64]$candidateOffset = [UInt64]$section.RawPointer + $delta
        if ($candidateOffset + [UInt64]$RequiredBytes -gt [UInt64]$FileLength) {
            throw "$Label is outside the file."
        }
        return [Int64]$candidateOffset
    }

    throw "$Label has an unmapped RVA."
}

function Read-AsciiNameAt {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [Int64] $Offset,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $bytes = [Collections.Generic.List[byte]]::new()
    for ($index = 0; $index -lt 260; ++$index) {
        if ($Offset + $index -ge $Reader.BaseStream.Length) {
            throw "$Label is truncated."
        }
        $Reader.BaseStream.Position = $Offset + $index
        $value = $Reader.ReadByte()
        if ($value -eq 0) {
            if ($bytes.Count -eq 0) {
                throw "$Label is empty."
            }
            return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
        }
        if ($value -lt 0x21 -or $value -gt 0x7e) {
            throw "$Label is not a canonical ASCII library name."
        }
        $bytes.Add($value)
    }

    throw "$Label exceeds the 259-byte library-name limit."
}

function Get-ImportedLibraryNames {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [object[]] $Sections,

        [Parameter(Mandatory = $true)]
        [UInt32] $DirectoryRva,

        [Parameter(Mandatory = $true)]
        [UInt32] $DirectorySize,

        [switch] $DelayImports
    )

    if ($DirectoryRva -eq 0 -or $DirectorySize -eq 0) {
        return @()
    }

    $descriptorSize = if ($DelayImports) { 32 } else { 20 }
    $descriptorFieldCount = $descriptorSize / 4
    $descriptorLimit = [Math]::Min(
        [int]($DirectorySize / $descriptorSize),
        4096)
    if ($descriptorLimit -lt 1) {
        throw 'The portable executable import directory is truncated.'
    }

    $names = [Collections.Generic.List[string]]::new()
    $sawTerminator = $false
    for ($descriptorIndex = 0;
         $descriptorIndex -lt $descriptorLimit;
         ++$descriptorIndex) {
        [UInt32]$descriptorRva =
            [UInt32]($DirectoryRva + ($descriptorIndex * $descriptorSize))
        $descriptorOffset = Convert-RvaToFileOffset `
            -Rva $descriptorRva `
            -RequiredBytes $descriptorSize `
            -Sections $Sections `
            -FileLength $Reader.BaseStream.Length `
            -Label 'The portable executable import descriptor'
        $fields = @(
            for ($fieldIndex = 0;
                 $fieldIndex -lt $descriptorFieldCount;
                 ++$fieldIndex) {
                Read-UInt32At `
                    -Reader $Reader `
                    -Offset ($descriptorOffset + ($fieldIndex * 4))
            })
        if (@($fields | Where-Object { $_ -ne 0 }).Count -eq 0) {
            $sawTerminator = $true
            break
        }

        if ($DelayImports -and $fields[0] -ne 1) {
            throw 'The delay-import descriptor does not use RVA-based fields.'
        }
        [UInt32]$nameRva = if ($DelayImports) { $fields[1] } else { $fields[3] }
        if ($nameRva -eq 0) {
            throw 'The portable executable import descriptor has no library name.'
        }
        $nameOffset = Convert-RvaToFileOffset `
            -Rva $nameRva `
            -RequiredBytes 1 `
            -Sections $Sections `
            -FileLength $Reader.BaseStream.Length `
            -Label 'The portable executable imported-library name'
        $names.Add((Read-AsciiNameAt `
            -Reader $Reader `
            -Offset $nameOffset `
            -Label 'The portable executable imported-library name'))
    }

    if (-not $sawTerminator) {
        throw 'The portable executable import descriptors are not terminated.'
    }
    return $names.ToArray()
}

function Assert-BaseRelocationDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [object[]] $Sections,

        [Parameter(Mandatory = $true)]
        [UInt32] $DirectoryRva,

        [Parameter(Mandatory = $true)]
        [UInt32] $DirectorySize,

        [Parameter(Mandatory = $true)]
        [UInt32] $SizeOfImage,

        [Parameter(Mandatory = $true)]
        [ValidateSet('x86', 'x64', 'ARM64')]
        [string] $Platform
    )

    if ($DirectoryRva -eq 0 -or
        $DirectorySize -lt 10 -or
        $DirectorySize -gt 16MB) {
        throw 'The portable executable has no bounded base-relocation directory.'
    }
    $directoryOffset = Convert-RvaToFileOffset `
        -Rva $DirectoryRva `
        -RequiredBytes $DirectorySize `
        -Sections $Sections `
        -FileLength $Reader.BaseStream.Length `
        -Label 'The portable executable base-relocation directory'
    [Int64]$consumed = 0
    $expectedRelocationType = if ($Platform -eq 'x86') { 3 } else { 10 }
    $sawExpectedRelocation = $false
    while ($consumed -lt $DirectorySize) {
        if ($DirectorySize - $consumed -lt 8) {
            throw 'The portable executable base-relocation directory has trailing bytes.'
        }
        $blockOffset = $directoryOffset + $consumed
        [UInt32]$pageRva = Read-UInt32At -Reader $Reader -Offset $blockOffset
        [UInt32]$blockSize =
            Read-UInt32At -Reader $Reader -Offset ($blockOffset + 4)
        if ($pageRva -eq 0 -or
            ($pageRva -band 0x0fff) -ne 0 -or
            $pageRva -ge $SizeOfImage -or
            $blockSize -lt 10 -or
            ($blockSize -band 1) -ne 0 -or
            $blockSize -gt $DirectorySize - $consumed) {
            throw 'The portable executable contains an invalid base-relocation block.'
        }

        $entryCount = [int](($blockSize - 8) / 2)
        for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
            $entry = Read-UInt16At `
                -Reader $Reader `
                -Offset ($blockOffset + 8 + ($entryIndex * 2))
            $relocationType = $entry -shr 12
            $pageOffset = $entry -band 0x0fff
            if ($relocationType -ne 0 -and
                $relocationType -ne $expectedRelocationType) {
                throw "The portable executable contains unexpected base-relocation type $relocationType."
            }
            if ($relocationType -eq $expectedRelocationType) {
                [UInt64]$targetRva = [UInt64]$pageRva + [UInt64]$pageOffset
                $requiredTargetBytes = if ($Platform -eq 'x86') { 4 } else { 8 }
                if ($targetRva + $requiredTargetBytes -gt $SizeOfImage) {
                    throw 'The portable executable contains an out-of-image base relocation.'
                }
                $sawExpectedRelocation = $true
            }
        }
        $consumed += $blockSize
    }
    if (-not $sawExpectedRelocation) {
        throw 'The portable executable base-relocation directory has no usable relocations.'
    }
}

function Assert-ExtendedDllCharacteristics {
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryReader] $Reader,

        [Parameter(Mandatory = $true)]
        [object[]] $Sections,

        [Parameter(Mandatory = $true)]
        [UInt32] $DirectoryRva,

        [Parameter(Mandatory = $true)]
        [UInt32] $DirectorySize,

        [Parameter(Mandatory = $true)]
        [ValidateSet('x86', 'x64', 'ARM64')]
        [string] $Platform
    )

    # cronixWG: the launcher hosts the .NET Framework CLR, which breaks under CET
    # shadow stacks (managed exceptions fail fast with 0xC0000409), so the launcher
    # must NOT claim CETCOMPAT on any platform.
    if ($DirectoryRva -eq 0 -and $DirectorySize -eq 0) {
        return
    }
    if ($DirectoryRva -eq 0 -or
        $DirectorySize -eq 0 -or
        ($DirectorySize % 28) -ne 0 -or
        $DirectorySize -gt (28 * 4096)) {
        throw 'The portable executable has an invalid debug directory.'
    }
    $directoryOffset = Convert-RvaToFileOffset `
        -Rva $DirectoryRva `
        -RequiredBytes $DirectorySize `
        -Sections $Sections `
        -FileLength $Reader.BaseStream.Length `
        -Label 'The portable executable debug directory'
    $extendedCharacteristicsCount = 0
    for ($entryOffset = [Int64]0;
         $entryOffset -lt $DirectorySize;
         $entryOffset += 28) {
        $debugEntryOffset = $directoryOffset + $entryOffset
        [UInt32]$debugType =
            Read-UInt32At -Reader $Reader -Offset ($debugEntryOffset + 12)
        if ($debugType -ne 20) {
            continue
        }
        $extendedCharacteristicsCount++
        [UInt32]$dataSize =
            Read-UInt32At -Reader $Reader -Offset ($debugEntryOffset + 16)
        [UInt32]$dataRva =
            Read-UInt32At -Reader $Reader -Offset ($debugEntryOffset + 20)
        [UInt32]$rawPointer =
            Read-UInt32At -Reader $Reader -Offset ($debugEntryOffset + 24)
        if ($dataSize -ne 4 -or
            $rawPointer -gt $Reader.BaseStream.Length - 4) {
            throw 'The extended DLL-characteristics debug record is malformed.'
        }
        $mappedDataOffset = Convert-RvaToFileOffset `
            -Rva $dataRva `
            -RequiredBytes 4 `
            -Sections $Sections `
            -FileLength $Reader.BaseStream.Length `
            -Label 'The extended DLL-characteristics data'
        if ($mappedDataOffset -ne $rawPointer) {
            throw 'The extended DLL-characteristics debug record has inconsistent offsets.'
        }
        $extendedCharacteristics =
            Read-UInt32At -Reader $Reader -Offset $rawPointer
        if ($extendedCharacteristics -ne 1) {
            throw "The extended DLL-characteristics value 0x$($extendedCharacteristics.ToString('x8')) is not the required CETCOMPAT policy."
        }
    }

    if ($extendedCharacteristicsCount -ne 0) {
        throw 'The launcher claims CETCOMPAT, which the hosted .NET Framework CLR does not support.'
    }
}

function Get-NativeBootstrapManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ($null -eq ('WireSockUI.Build.NativeResourceReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WireSockUI.Build
{
    public static class NativeResourceReader
    {
        private const uint LoadLibraryAsImageResource = 0x00000020;
        private const uint LoadLibraryAsDataFileExclusive = 0x00000040;
        private const int ManifestResourceIdentifier = 1;
        private const int ManifestResourceType = 24;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(
            string fileName,
            IntPtr file,
            uint flags);

        [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
        private static extern IntPtr FindResource(
            IntPtr module,
            IntPtr name,
            IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resourceData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        public static byte[] ReadManifest(string path)
        {
            IntPtr module = LoadLibraryEx(
                path,
                IntPtr.Zero,
                LoadLibraryAsImageResource | LoadLibraryAsDataFileExclusive);
            if (module == IntPtr.Zero)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to map the launcher as a non-executable image resource.");

            try
            {
                IntPtr resource = FindResource(
                    module,
                    new IntPtr(ManifestResourceIdentifier),
                    new IntPtr(ManifestResourceType));
                if (resource == IntPtr.Zero)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The launcher has no embedded application manifest.");

                uint size = SizeofResource(module, resource);
                if (size == 0 || size > 1024 * 1024)
                    throw new InvalidOperationException(
                        "The embedded application manifest has an invalid size.");

                IntPtr loaded = LoadResource(module, resource);
                if (loaded == IntPtr.Zero)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The embedded application manifest could not be loaded.");

                IntPtr data = LockResource(loaded);
                if (data == IntPtr.Zero)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The embedded application manifest could not be read.");

                byte[] bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, checked((int)size));
                return bytes;
            }
            finally
            {
                FreeLibrary(module);
            }
        }
    }
}
'@
    }

    $bytes = [WireSockUI.Build.NativeResourceReader]::ReadManifest($Path)
    $offset = 0
    $encoding = $null
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xef -and
        $bytes[1] -eq 0xbb -and
        $bytes[2] -eq 0xbf) {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $offset = 3
    }
    elseif ($bytes.Length -ge 2 -and
        $bytes[0] -eq 0xff -and
        $bytes[1] -eq 0xfe) {
        $encoding = [Text.UnicodeEncoding]::new($false, $true, $true)
        $offset = 2
    }
    elseif ($bytes.Length -ge 2 -and
        $bytes[0] -eq 0xfe -and
        $bytes[1] -eq 0xff) {
        $encoding = [Text.UnicodeEncoding]::new($true, $true, $true)
        $offset = 2
    }
    else {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
    }

    $manifestText = $encoding.GetString(
        $bytes,
        $offset,
        $bytes.Length - $offset)
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stringReader = New-Object IO.StringReader $manifestText
    $xmlReader = $null
    try {
        $xmlReader = [Xml.XmlReader]::Create($stringReader, $settings)
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($xmlReader)
        return $document
    }
    finally {
        if ($null -ne $xmlReader) {
            $xmlReader.Dispose()
        }
        $stringReader.Dispose()
    }
}

function Get-PortableExecutablePlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $Path
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($resolvedPath)) {
        throw "Portable executable '$resolvedPath' does not exist."
    }

    $stream = [IO.File]::Open(
        $resolvedPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $reader = New-Object IO.BinaryReader $stream
        try {
            if ($stream.Length -lt 64 -or
                (Read-UInt16At -Reader $reader -Offset 0) -ne 0x5a4d) {
                throw "Portable executable '$resolvedPath' has no valid DOS header."
            }
            [Int64]$peOffset = Read-UInt32At -Reader $reader -Offset 0x3c
            if ($peOffset -gt $stream.Length - 24 -or
                (Read-UInt32At -Reader $reader -Offset $peOffset) -ne 0x00004550) {
                throw "Portable executable '$resolvedPath' has no valid PE header."
            }

            $machine = Read-UInt16At -Reader $reader -Offset ($peOffset + 4)
            switch ($machine) {
                0x014c { return 'x86' }
                0x8664 { return 'x64' }
                0xaa64 { return 'ARM64' }
                default {
                    throw "Portable executable '$resolvedPath' has unsupported machine type 0x$($machine.ToString('x4'))."
                }
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

function Assert-ManagedAssemblyPlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet('x86', 'x64', 'ARM64')]
        [string] $ExpectedPlatform
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $actualPlatform = Get-PortableExecutablePlatform -Path $resolvedPath
    if ($actualPlatform -cne $ExpectedPlatform) {
        throw "Managed assembly '$resolvedPath' targets $actualPlatform, not requested architecture $ExpectedPlatform."
    }

    $stream = [IO.File]::Open(
        $resolvedPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $reader = New-Object IO.BinaryReader $stream
        try {
            [Int64]$peOffset = Read-UInt32At -Reader $reader -Offset 0x3c
            $coffOffset = $peOffset + 4
            $numberOfSections = Read-UInt16At -Reader $reader -Offset ($coffOffset + 2)
            $sizeOfOptionalHeader = Read-UInt16At -Reader $reader -Offset ($coffOffset + 16)
            if ($numberOfSections -lt 1 -or $numberOfSections -gt 96) {
                throw "Managed assembly '$resolvedPath' has an invalid section count."
            }

            $optionalOffset = $peOffset + 24
            $optionalEnd = $optionalOffset + $sizeOfOptionalHeader
            if ($optionalEnd -gt $stream.Length) {
                throw "Managed assembly '$resolvedPath' has a truncated optional header."
            }
            $magic = Read-UInt16At -Reader $reader -Offset $optionalOffset
            $expectedMagic = if ($ExpectedPlatform -eq 'x86') { 0x010b } else { 0x020b }
            if ($magic -ne $expectedMagic) {
                throw "Managed assembly '$resolvedPath' has the wrong PE format for $ExpectedPlatform."
            }

            $dataDirectoryOffset = if ($magic -eq 0x010b) {
                $optionalOffset + 96
            }
            else {
                $optionalOffset + 112
            }
            $numberOfRvaAndSizesOffset = if ($magic -eq 0x010b) {
                $optionalOffset + 92
            }
            else {
                $optionalOffset + 108
            }
            $numberOfRvaAndSizes =
                Read-UInt32At -Reader $reader -Offset $numberOfRvaAndSizesOffset
            if ($numberOfRvaAndSizes -le 14 -or
                $dataDirectoryOffset + (15 * 8) -gt $optionalEnd) {
                throw "Managed assembly '$resolvedPath' has no CLR header."
            }

            $sectionTableOffset = $optionalEnd
            if ($sectionTableOffset + ([Int64]$numberOfSections * 40) -gt $stream.Length) {
                throw "Managed assembly '$resolvedPath' has a truncated section table."
            }
            $sections = [Collections.Generic.List[object]]::new()
            for ($sectionIndex = 0; $sectionIndex -lt $numberOfSections; ++$sectionIndex) {
                $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
                $sections.Add([pscustomobject]@{
                    VirtualSize =
                        Read-UInt32At -Reader $reader -Offset ($sectionOffset + 8)
                    VirtualAddress =
                        Read-UInt32At -Reader $reader -Offset ($sectionOffset + 12)
                    RawSize =
                        Read-UInt32At -Reader $reader -Offset ($sectionOffset + 16)
                    RawPointer =
                        Read-UInt32At -Reader $reader -Offset ($sectionOffset + 20)
                })
            }

            $clrDirectoryOffset = $dataDirectoryOffset + (14 * 8)
            [UInt32]$clrRva =
                Read-UInt32At -Reader $reader -Offset $clrDirectoryOffset
            [UInt32]$clrSize =
                Read-UInt32At -Reader $reader -Offset ($clrDirectoryOffset + 4)
            if ($clrRva -eq 0 -or $clrSize -lt 72) {
                throw "Managed assembly '$resolvedPath' has an incomplete CLR header."
            }
            $clrOffset = Convert-RvaToFileOffset `
                -Rva $clrRva `
                -RequiredBytes 72 `
                -Sections $sections.ToArray() `
                -FileLength $stream.Length `
                -Label "Managed assembly '$resolvedPath' CLR header"
            $clrHeaderSize = Read-UInt32At -Reader $reader -Offset $clrOffset
            if ($clrHeaderSize -lt 72 -or $clrHeaderSize -gt $clrSize) {
                throw "Managed assembly '$resolvedPath' has an invalid CLR header size."
            }

            $corFlags = Read-UInt32At -Reader $reader -Offset ($clrOffset + 16)
            $ilOnly = 0x00000001
            $requires32Bit = 0x00000002
            $nativeEntryPoint = 0x00000010
            $prefers32Bit = 0x00020000
            if (($corFlags -band $ilOnly) -eq 0 -or
                ($corFlags -band $nativeEntryPoint) -ne 0) {
                throw "Managed assembly '$resolvedPath' is not a pure IL assembly."
            }
            if ($ExpectedPlatform -eq 'x86') {
                if (($corFlags -band $requires32Bit) -eq 0 -or
                    ($corFlags -band $prefers32Bit) -ne 0) {
                    throw "Managed assembly '$resolvedPath' does not require the x86 CLR."
                }
            }
            elseif (($corFlags -band ($requires32Bit -bor $prefers32Bit)) -ne 0) {
                throw "Managed assembly '$resolvedPath' has incompatible 32-bit CLR flags."
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Platform = $actualPlatform
    }
}

function Assert-NativeBootstrap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet('x86', 'x64', 'ARM64')]
        [string] $ExpectedPlatform,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')]
        [string] $ExpectedVersion,

        [switch] $RequireProductionBuild
    )

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Native bootstrap validation requires Windows.'
    }

    $parsedVersion = $null
    if (-not [Version]::TryParse($ExpectedVersion, [ref]$parsedVersion)) {
        throw "Expected version '$ExpectedVersion' is invalid."
    }
    $versionParts = @(
        $parsedVersion.Major,
        [Math]::Max(0, $parsedVersion.Minor),
        [Math]::Max(0, $parsedVersion.Build),
        [Math]::Max(0, $parsedVersion.Revision))
    foreach ($versionPart in $versionParts) {
        if ($versionPart -gt [UInt16]::MaxValue) {
            throw "Expected version '$ExpectedVersion' contains a component larger than 65535."
        }
    }
    $normalizedExpectedVersion = $versionParts -join '.'

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($resolvedPath)) {
        throw "Native launcher '$resolvedPath' does not exist."
    }

    $stream = [IO.File]::Open(
        $resolvedPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $reader = New-Object IO.BinaryReader $stream
        try {
            if ($stream.Length -lt 64 -or
                (Read-UInt16At -Reader $reader -Offset 0) -ne 0x5a4d) {
                throw "Native launcher '$resolvedPath' has no valid DOS header."
            }

            [Int64]$peOffset = Read-UInt32At -Reader $reader -Offset 0x3c
            if ($peOffset -gt $stream.Length - 24 -or
                (Read-UInt32At -Reader $reader -Offset $peOffset) -ne 0x00004550) {
                throw "Native launcher '$resolvedPath' has no valid PE header."
            }

            $coffOffset = $peOffset + 4
            $machine = Read-UInt16At -Reader $reader -Offset $coffOffset
            $numberOfSections = Read-UInt16At -Reader $reader -Offset ($coffOffset + 2)
            $sizeOfOptionalHeader = Read-UInt16At -Reader $reader -Offset ($coffOffset + 16)
            $characteristics = Read-UInt16At -Reader $reader -Offset ($coffOffset + 18)
            if ($numberOfSections -lt 1 -or $numberOfSections -gt 96) {
                throw "Native launcher '$resolvedPath' has an invalid section count."
            }
            if (($characteristics -band 0x0002) -eq 0 -or
                ($characteristics -band 0x2000) -ne 0 -or
                ($characteristics -band 0x0001) -ne 0) {
                throw "Native launcher '$resolvedPath' is not a relocatable non-DLL executable image."
            }

            $optionalOffset = $peOffset + 24
            $optionalEnd = $optionalOffset + $sizeOfOptionalHeader
            if ($optionalEnd -gt $stream.Length) {
                throw "Native launcher '$resolvedPath' has a truncated optional header."
            }

            $magic = Read-UInt16At -Reader $reader -Offset $optionalOffset
            $expectedMachine = switch ($ExpectedPlatform) {
                'x86' { 0x014c }
                'x64' { 0x8664 }
                'ARM64' { 0xaa64 }
            }
            $expectedMagic = if ($ExpectedPlatform -eq 'x86') { 0x010b } else { 0x020b }
            $minimumOptionalHeaderSize = if ($magic -eq 0x010b) { 224 } else { 240 }
            if ($machine -ne $expectedMachine -or $magic -ne $expectedMagic) {
                throw "Native launcher '$resolvedPath' does not target $ExpectedPlatform."
            }
            if ($sizeOfOptionalHeader -lt $minimumOptionalHeaderSize) {
                throw "Native launcher '$resolvedPath' has an incomplete optional header."
            }
            [UInt64]$imageBase = if ($magic -eq 0x010b) {
                Read-UInt32At -Reader $reader -Offset ($optionalOffset + 28)
            }
            else {
                Read-UInt64At -Reader $reader -Offset ($optionalOffset + 24)
            }
            [UInt32]$sizeOfImage =
                Read-UInt32At -Reader $reader -Offset ($optionalOffset + 56)
            [UInt32]$sectionAlignment =
                Read-UInt32At -Reader $reader -Offset ($optionalOffset + 32)
            [UInt32]$fileAlignment =
                Read-UInt32At -Reader $reader -Offset ($optionalOffset + 36)
            [UInt32]$sizeOfHeaders =
                Read-UInt32At -Reader $reader -Offset ($optionalOffset + 60)
            if ($imageBase -eq 0 -or
                $sizeOfImage -lt 4096 -or
                $sectionAlignment -lt 4096 -or
                ($sectionAlignment -band ($sectionAlignment - 1)) -ne 0 -or
                $fileAlignment -lt 512 -or
                $fileAlignment -gt 65536 -or
                ($fileAlignment -band ($fileAlignment - 1)) -ne 0 -or
                $sectionAlignment -lt $fileAlignment -or
                $sizeOfHeaders -lt
                    $optionalEnd + ([Int64]$numberOfSections * 40) -or
                $sizeOfHeaders -gt $stream.Length) {
                throw "Native launcher '$resolvedPath' has invalid image bounds."
            }

            $subsystem = Read-UInt16At -Reader $reader -Offset ($optionalOffset + 0x44)
            if ($subsystem -ne 2) {
                throw "Native launcher '$resolvedPath' is not a Windows GUI executable."
            }

            $dllCharacteristics = Read-UInt16At -Reader $reader -Offset ($optionalOffset + 0x46)
            $requiredDllCharacteristics = 0x0040 -bor 0x0100 -bor 0x4000
            if ($magic -eq 0x020b) {
                $requiredDllCharacteristics = $requiredDllCharacteristics -bor 0x0020
            }
            if (($dllCharacteristics -band $requiredDllCharacteristics) -ne
                $requiredDllCharacteristics) {
                throw "Native launcher '$resolvedPath' is missing required ASLR, NX, high-entropy, or Control Flow Guard PE flags."
            }

            $dataDirectoryOffset = if ($magic -eq 0x010b) {
                $optionalOffset + 96
            }
            else {
                $optionalOffset + 112
            }
            $numberOfRvaAndSizesOffset = if ($magic -eq 0x010b) {
                $optionalOffset + 92
            }
            else {
                $optionalOffset + 108
            }
            $numberOfRvaAndSizes =
                Read-UInt32At -Reader $reader -Offset $numberOfRvaAndSizesOffset
            if ($numberOfRvaAndSizes -le 13 -or
                $dataDirectoryOffset + (14 * 8) -gt $optionalEnd) {
                throw "Native launcher '$resolvedPath' has no load-configuration data directory."
            }

            $importDirectoryOffset = $dataDirectoryOffset + 8
            [UInt32]$importRva =
                Read-UInt32At -Reader $reader -Offset $importDirectoryOffset
            [UInt32]$importSize =
                Read-UInt32At -Reader $reader -Offset ($importDirectoryOffset + 4)
            $relocationDirectoryOffset = $dataDirectoryOffset + (5 * 8)
            [UInt32]$relocationRva =
                Read-UInt32At -Reader $reader -Offset $relocationDirectoryOffset
            [UInt32]$relocationSize =
                Read-UInt32At -Reader $reader -Offset ($relocationDirectoryOffset + 4)
            $debugDirectoryOffset = $dataDirectoryOffset + (6 * 8)
            [UInt32]$debugRva =
                Read-UInt32At -Reader $reader -Offset $debugDirectoryOffset
            [UInt32]$debugSize =
                Read-UInt32At -Reader $reader -Offset ($debugDirectoryOffset + 4)
            $loadConfigDirectoryOffset = $dataDirectoryOffset + (10 * 8)
            [UInt32]$loadConfigRva =
                Read-UInt32At -Reader $reader -Offset $loadConfigDirectoryOffset
            [UInt32]$loadConfigSize =
                Read-UInt32At -Reader $reader -Offset ($loadConfigDirectoryOffset + 4)
            $delayImportDirectoryOffset = $dataDirectoryOffset + (13 * 8)
            [UInt32]$delayImportRva =
                Read-UInt32At -Reader $reader -Offset $delayImportDirectoryOffset
            [UInt32]$delayImportSize =
                Read-UInt32At -Reader $reader -Offset ($delayImportDirectoryOffset + 4)
            $dependentLoadFlagsOffset = if ($magic -eq 0x010b) { 0x36 } else { 0x4e }
            $requiredLoadConfigBytes = $dependentLoadFlagsOffset + 2
            if ($loadConfigRva -eq 0 -or
                $loadConfigSize -lt $requiredLoadConfigBytes) {
                throw "Native launcher '$resolvedPath' has no complete dependent-load policy."
            }

            $sectionTableOffset = $optionalEnd
            if ($sectionTableOffset + ([Int64]$numberOfSections * 40) -gt $stream.Length) {
                throw "Native launcher '$resolvedPath' has a truncated section table."
            }

            $sections = [Collections.Generic.List[object]]::new()
            for ($sectionIndex = 0; $sectionIndex -lt $numberOfSections; $sectionIndex++) {
                $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
                [UInt32]$virtualSize =
                    Read-UInt32At -Reader $reader -Offset ($sectionOffset + 8)
                [UInt32]$virtualAddress =
                    Read-UInt32At -Reader $reader -Offset ($sectionOffset + 12)
                [UInt32]$rawSize =
                    Read-UInt32At -Reader $reader -Offset ($sectionOffset + 16)
                [UInt32]$rawPointer =
                    Read-UInt32At -Reader $reader -Offset ($sectionOffset + 20)
                [UInt32]$sectionCharacteristics =
                    Read-UInt32At -Reader $reader -Offset ($sectionOffset + 36)
                $sections.Add([pscustomobject]@{
                    VirtualSize = $virtualSize
                    VirtualAddress = $virtualAddress
                    RawSize = $rawSize
                    RawPointer = $rawPointer
                    Characteristics = $sectionCharacteristics
                })
            }
            [UInt64]$previousVirtualEnd = 0
            foreach ($section in @($sections | Sort-Object VirtualAddress)) {
                [UInt64]$virtualSpan = [Math]::Max(
                    [UInt64]$section.VirtualSize,
                    [UInt64]$section.RawSize)
                [UInt64]$virtualEnd = [UInt64]$section.VirtualAddress + $virtualSpan
                if ($virtualSpan -eq 0 -or
                    ([UInt64]$section.VirtualAddress % $sectionAlignment) -ne 0 -or
                    [UInt64]$section.VirtualAddress -lt $previousVirtualEnd -or
                    $virtualEnd -gt $sizeOfImage -or
                    (($section.Characteristics -band 0x20000000) -ne 0 -and
                        ($section.Characteristics -band 0x80000000) -ne 0)) {
                    throw "Native launcher '$resolvedPath' has overlapping, misaligned, out-of-image, or writable-executable sections."
                }
                $previousVirtualEnd = $virtualEnd
            }
            [UInt64]$previousRawEnd = $sizeOfHeaders
            foreach ($section in @(
                    $sections |
                        Where-Object { $_.RawSize -ne 0 } |
                        Sort-Object RawPointer)) {
                [UInt64]$rawEnd =
                    [UInt64]$section.RawPointer + [UInt64]$section.RawSize
                if (($section.RawPointer % $fileAlignment) -ne 0 -or
                    [UInt64]$section.RawPointer -lt $previousRawEnd -or
                    $rawEnd -gt [UInt64]$stream.Length) {
                    throw "Native launcher '$resolvedPath' has overlapping, misaligned, or truncated raw sections."
                }
                $previousRawEnd = $rawEnd
            }
            Assert-BaseRelocationDirectory `
                -Reader $reader `
                -Sections $sections.ToArray() `
                -DirectoryRva $relocationRva `
                -DirectorySize $relocationSize `
                -SizeOfImage $sizeOfImage `
                -Platform $ExpectedPlatform
            Assert-ExtendedDllCharacteristics `
                -Reader $reader `
                -Sections $sections.ToArray() `
                -DirectoryRva $debugRva `
                -DirectorySize $debugSize `
                -Platform $ExpectedPlatform

            $loadConfigFileOffset = Convert-RvaToFileOffset `
                -Rva $loadConfigRva `
                -RequiredBytes $requiredLoadConfigBytes `
                -Sections $sections.ToArray() `
                -FileLength $stream.Length `
                -Label "Native launcher '$resolvedPath' load configuration"

            $loadConfigStructureSize =
                Read-UInt32At -Reader $reader -Offset ([Int64]$loadConfigFileOffset)
            if ($loadConfigStructureSize -lt $requiredLoadConfigBytes -or
                $loadConfigStructureSize -gt 4096) {
                throw "Native launcher '$resolvedPath' has a truncated load-configuration structure."
            }
            $loadConfigFileOffset = Convert-RvaToFileOffset `
                -Rva $loadConfigRva `
                -RequiredBytes $loadConfigStructureSize `
                -Sections $sections.ToArray() `
                -FileLength $stream.Length `
                -Label "Native launcher '$resolvedPath' complete load configuration"
            $dependentLoadFlags = Read-UInt16At `
                -Reader $reader `
                -Offset ([Int64]$loadConfigFileOffset + $dependentLoadFlagsOffset)
            if ($dependentLoadFlags -ne 0x0800) {
                throw "Native launcher '$resolvedPath' does not constrain static dependent DLLs to System32 (DependentLoadFlags=0x$($dependentLoadFlags.ToString('x4')))."
            }

            $guardCheckOffset = if ($magic -eq 0x010b) { 0x48 } else { 0x70 }
            $guardDispatchOffset = if ($magic -eq 0x010b) { 0x4c } else { 0x78 }
            $guardTableOffset = if ($magic -eq 0x010b) { 0x50 } else { 0x80 }
            $guardCountOffset = if ($magic -eq 0x010b) { 0x54 } else { 0x88 }
            $guardFlagsOffset = if ($magic -eq 0x010b) { 0x58 } else { 0x90 }
            $guardRequiredBytes = $guardFlagsOffset + 4
            if ($loadConfigStructureSize -lt $guardRequiredBytes) {
                throw "Native launcher '$resolvedPath' has no complete Control Flow Guard load configuration."
            }
            $loadConfigFileOffset = Convert-RvaToFileOffset `
                -Rva $loadConfigRva `
                -RequiredBytes $guardRequiredBytes `
                -Sections $sections.ToArray() `
                -FileLength $stream.Length `
                -Label "Native launcher '$resolvedPath' Control Flow Guard configuration"
            [UInt64]$guardCheckPointer = if ($magic -eq 0x010b) {
                Read-UInt32At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardCheckOffset)
            }
            else {
                Read-UInt64At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardCheckOffset)
            }
            [UInt64]$guardDispatchPointer = if ($magic -eq 0x010b) {
                Read-UInt32At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardDispatchOffset)
            }
            else {
                Read-UInt64At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardDispatchOffset)
            }
            [UInt64]$guardFunctionTable = if ($magic -eq 0x010b) {
                Read-UInt32At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardTableOffset)
            }
            else {
                Read-UInt64At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardTableOffset)
            }
            [UInt64]$guardFunctionCount = if ($magic -eq 0x010b) {
                Read-UInt32At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardCountOffset)
            }
            else {
                Read-UInt64At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + $guardCountOffset)
            }
            [UInt32]$guardFlags = Read-UInt32At `
                -Reader $reader `
                -Offset ($loadConfigFileOffset + $guardFlagsOffset)
            [UInt64]$imageEnd = $imageBase + [UInt64]$sizeOfImage
            $guardPointers = @($guardCheckPointer, $guardFunctionTable)
            if ($ExpectedPlatform -ne 'x86') {
                $guardPointers += $guardDispatchPointer
            }
            if (@($guardPointers | Where-Object {
                        [UInt64]$_ -lt $imageBase -or
                        [UInt64]$_ -ge $imageEnd
                    }).Count -ne 0 -or
                $guardFunctionCount -lt 1 -or
                $guardFunctionCount -gt 1000000 -or
                ($guardFlags -band 0x00000500) -ne 0x00000500) {
                throw "Native launcher '$resolvedPath' has incomplete or invalid Control Flow Guard metadata."
            }

            $pointerBytes = if ($magic -eq 0x010b) { 4 } else { 8 }
            foreach ($guardPointer in $guardPointers) {
                [UInt64]$guardPointerRva64 = [UInt64]$guardPointer - $imageBase
                if ($guardPointerRva64 -gt [UInt32]::MaxValue) {
                    throw "Native launcher '$resolvedPath' has an out-of-range Control Flow Guard pointer."
                }
                [void](Convert-RvaToFileOffset `
                    -Rva ([UInt32]$guardPointerRva64) `
                    -RequiredBytes $pointerBytes `
                    -Sections $sections.ToArray() `
                    -FileLength $stream.Length `
                    -Label "Native launcher '$resolvedPath' Control Flow Guard pointer")
            }

            $guardEntryExtraBytes = [int](($guardFlags -band 0xf0000000) -shr 28)
            [UInt64]$guardEntryBytes = [UInt64](4 + $guardEntryExtraBytes)
            if ($guardFunctionCount -gt [UInt64]::MaxValue / $guardEntryBytes) {
                throw "Native launcher '$resolvedPath' has overflowing Control Flow Guard table dimensions."
            }
            [UInt64]$guardTableBytes64 = $guardFunctionCount * $guardEntryBytes
            if ($guardTableBytes64 -gt 16MB -or
                $guardFunctionTable -lt $imageBase) {
                throw "Native launcher '$resolvedPath' has an unbounded Control Flow Guard function table."
            }
            [UInt64]$guardTableRva64 = $guardFunctionTable - $imageBase
            if ($guardTableRva64 -gt [UInt32]::MaxValue -or
                $guardTableBytes64 -gt [UInt32]::MaxValue) {
                throw "Native launcher '$resolvedPath' has an out-of-range Control Flow Guard function table."
            }
            $guardTableFileOffset = Convert-RvaToFileOffset `
                -Rva ([UInt32]$guardTableRva64) `
                -RequiredBytes ([UInt32]$guardTableBytes64) `
                -Sections $sections.ToArray() `
                -FileLength $stream.Length `
                -Label "Native launcher '$resolvedPath' Control Flow Guard function table"
            [UInt32]$previousGuardTarget = 0
            for ([UInt64]$guardIndex = 0;
                 $guardIndex -lt $guardFunctionCount;
                 $guardIndex++) {
                [UInt32]$guardTarget = Read-UInt32At `
                    -Reader $reader `
                    -Offset ($guardTableFileOffset + [Int64]($guardIndex * $guardEntryBytes))
                if ($guardTarget -eq 0 -or
                    $guardTarget -ge $sizeOfImage -or
                    ($guardIndex -gt 0 -and $guardTarget -le $previousGuardTarget)) {
                    throw "Native launcher '$resolvedPath' has an invalid or unsorted Control Flow Guard target table."
                }
                $previousGuardTarget = $guardTarget
            }

            if ($ExpectedPlatform -eq 'x86') {
                $safeSehRequiredBytes = 0x48
                if ($loadConfigStructureSize -lt $safeSehRequiredBytes) {
                    throw "Native launcher '$resolvedPath' has no complete SafeSEH load configuration."
                }
                [UInt64]$safeSehTable = Read-UInt32At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + 0x40)
                [UInt64]$safeSehCount = Read-UInt32At `
                    -Reader $reader `
                    -Offset ($loadConfigFileOffset + 0x44)
                if ($safeSehTable -lt $imageBase -or
                    $safeSehTable -ge $imageEnd -or
                    $safeSehCount -lt 1 -or
                    $safeSehCount -gt 1000000) {
                    throw "Native launcher '$resolvedPath' has invalid SafeSEH metadata."
                }
                [UInt64]$safeSehTableRva64 = $safeSehTable - $imageBase
                [UInt64]$safeSehTableBytes64 = $safeSehCount * 4
                if ($safeSehTableRva64 -gt [UInt32]::MaxValue -or
                    $safeSehTableBytes64 -gt 16MB) {
                    throw "Native launcher '$resolvedPath' has an unbounded SafeSEH table."
                }
                $safeSehFileOffset = Convert-RvaToFileOffset `
                    -Rva ([UInt32]$safeSehTableRva64) `
                    -RequiredBytes ([UInt32]$safeSehTableBytes64) `
                    -Sections $sections.ToArray() `
                    -FileLength $stream.Length `
                    -Label "Native launcher '$resolvedPath' SafeSEH table"
                [UInt32]$previousSafeSehTarget = 0
                for ([UInt64]$safeSehIndex = 0;
                     $safeSehIndex -lt $safeSehCount;
                     $safeSehIndex++) {
                    [UInt32]$safeSehTarget = Read-UInt32At `
                        -Reader $reader `
                        -Offset ($safeSehFileOffset + [Int64]($safeSehIndex * 4))
                    if ($safeSehTarget -eq 0 -or
                        $safeSehTarget -ge $sizeOfImage -or
                        ($safeSehIndex -gt 0 -and
                            $safeSehTarget -le $previousSafeSehTarget)) {
                        throw "Native launcher '$resolvedPath' has an invalid or unsorted SafeSEH table."
                    }
                    $previousSafeSehTarget = $safeSehTarget
                }
            }

            $normalImports = @(Get-ImportedLibraryNames `
                -Reader $reader `
                -Sections $sections.ToArray() `
                -DirectoryRva $importRva `
                -DirectorySize $importSize)
            $delayImports = @(Get-ImportedLibraryNames `
                -Reader $reader `
                -Sections $sections.ToArray() `
                -DirectoryRva $delayImportRva `
                -DirectorySize $delayImportSize `
                -DelayImports)
            if ($normalImports -icontains 'bcrypt.dll') {
                throw "Native launcher '$resolvedPath' imports bcrypt.dll before its DLL search policy is established."
            }
            $allowedNormalImports =
                [Collections.Generic.HashSet[string]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
            foreach ($allowedImport in @(
                    'advapi32.dll',
                    'kernel32.dll',
                    'ole32.dll',
                    'user32.dll')) {
                [void]$allowedNormalImports.Add($allowedImport)
            }
            foreach ($normalImport in $normalImports) {
                if (-not $allowedNormalImports.Contains($normalImport)) {
                    throw "Native launcher '$resolvedPath' has unreviewed pre-entry-point import '$normalImport'."
                }
            }
            if (@($delayImports | Where-Object {
                        $_ -ieq 'bcrypt.dll'
                    }).Count -ne 1) {
                throw "Native launcher '$resolvedPath' does not delay-load exactly one bcrypt.dll dependency."
            }
            if ($delayImports.Count -ne 1) {
                throw "Native launcher '$resolvedPath' has an unreviewed delay-loaded dependency."
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $manifest = Get-NativeBootstrapManifest -Path $resolvedPath
    $executionLevel = @(
        $manifest.SelectNodes(
            "//*[local-name()='requestedExecutionLevel']"))
    if ($executionLevel.Count -ne 1 -or
        $executionLevel[0].level -cne 'requireAdministrator' -or
        $executionLevel[0].uiAccess -cne 'false') {
        throw "Native launcher '$resolvedPath' does not enforce requireAdministrator with uiAccess disabled."
    }
    $commonControls = @(
        $manifest.SelectNodes(
            "//*[local-name()='assemblyIdentity' and @name='Microsoft.Windows.Common-Controls' and @version='6.0.0.0']"))
    if ($commonControls.Count -ne 1) {
        throw "Native launcher '$resolvedPath' does not activate common-controls v6."
    }
    $dpiAwareness = @(
        $manifest.SelectNodes(
            "//*[local-name()='dpiAwareness']"))
    if ($dpiAwareness.Count -ne 1 -or
        -not $dpiAwareness[0].InnerText.Contains('PerMonitorV2')) {
        throw "Native launcher '$resolvedPath' does not enable PerMonitorV2 DPI awareness."
    }
    $longPathAware = @(
        $manifest.SelectNodes(
            "//*[local-name()='longPathAware']"))
    if ($longPathAware.Count -ne 1 -or
        $longPathAware[0].InnerText -cne 'true') {
        throw "Native launcher '$resolvedPath' does not enable long-path awareness."
    }

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedPath)
    if ($versionInfo.FileDescription -cne 'WireSock UI secure launcher' -or
        $versionInfo.ProductName -cne 'WireSock UI' -or
        $versionInfo.CompanyName -cne 'WireSockUI' -or
        $versionInfo.InternalName -cne 'WireSockUI' -or
        $versionInfo.OriginalFilename -cne 'WireSockUI.exe' -or
        $versionInfo.FileVersion -cne $normalizedExpectedVersion -or
        $versionInfo.ProductVersion -cne $normalizedExpectedVersion) {
        throw "Native launcher '$resolvedPath' VERSIONINFO does not match the expected product metadata."
    }
    if ($RequireProductionBuild -and $versionInfo.IsDebug) {
        throw "Production native launcher '$resolvedPath' contains the VS_FF_DEBUG development flag."
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Platform = $ExpectedPlatform
        Version = $normalizedExpectedVersion
        IsDebug = $versionInfo.IsDebug
        DependentLoadFlags = '0x0800'
    }
}

Export-ModuleMember `
    -Function Assert-NativeBootstrap,
              Assert-ManagedAssemblyPlatform,
              Get-PortableExecutablePlatform
