#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
if (-not $root.PSIsContainer -or
    ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Signature-removal root '$($root.FullName)' must be an ordinary directory."
}

function Test-HasMzHeader {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileInfo] $File
    )

    $stream = [IO.File]::OpenRead($File.FullName)
    try {
        if ($stream.Length -lt 2) {
            return $false
        }

        return $stream.ReadByte() -eq 0x4d -and
            $stream.ReadByte() -eq 0x5a
    }
    finally {
        $stream.Dispose()
    }
}

$maximumEntries = 4096
$modules = [Collections.Generic.List[IO.FileInfo]]::new()
$pendingDirectories = [Collections.Generic.Queue[IO.DirectoryInfo]]::new()
$pendingDirectories.Enqueue([IO.DirectoryInfo]$root)
$entryCount = 0

while ($pendingDirectories.Count -gt 0) {
    $directory = $pendingDirectories.Dequeue()
    foreach ($child in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
        if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Signature-removal input '$($child.FullName)' must not contain reparse points."
        }

        $entryCount++
        if ($entryCount -gt $maximumEntries) {
            throw "Signature-removal input exceeds the $maximumEntries-entry limit."
        }

        if ($child.PSIsContainer) {
            $pendingDirectories.Enqueue([IO.DirectoryInfo]$child)
        }
        elseif (Test-HasMzHeader -File ([IO.FileInfo]$child)) {
            $modules.Add([IO.FileInfo]$child)
        }
    }
}

$modules = @($modules | Sort-Object FullName)
if ($modules.Count -lt 1 -or $modules.Count -gt 4096) {
    throw "Signature-removal input contains $($modules.Count) modules; expected 1..4096."
}

$removedCount = 0
foreach ($module in $modules) {
    if (($module.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Signature-removal module '$($module.FullName)' must not be a reparse point."
    }

    $stream = [IO.File]::Open(
        $module.FullName,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        if ($stream.Length -lt 512 -or $stream.Length -gt 512MB) {
            throw "Module '$($module.FullName)' has unsupported length $($stream.Length)."
        }

        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "Module '$($module.FullName)' has no DOS PE header."
            }
            $stream.Position = 0x3c
            [UInt32]$peOffset = $reader.ReadUInt32()
            if ($peOffset -gt $stream.Length - 256) {
                throw "Module '$($module.FullName)' has an invalid PE offset."
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "Module '$($module.FullName)' has no PE signature."
            }
            $optionalHeaderOffset = [Int64]$peOffset + 24
            $stream.Position = $optionalHeaderOffset
            $magic = $reader.ReadUInt16()
            if ($magic -eq 0x10b) {
                $numberOfDirectoriesOffset = $optionalHeaderOffset + 92
                $dataDirectoryOffset = $optionalHeaderOffset + 96
            }
            elseif ($magic -eq 0x20b) {
                $numberOfDirectoriesOffset = $optionalHeaderOffset + 108
                $dataDirectoryOffset = $optionalHeaderOffset + 112
            }
            else {
                throw "Module '$($module.FullName)' has unsupported PE optional-header magic."
            }
            $stream.Position = $numberOfDirectoriesOffset
            if ($reader.ReadUInt32() -lt 5) {
                continue
            }
            $certificateDirectoryOffset = $dataDirectoryOffset + (4 * 8)
            if ($certificateDirectoryOffset -gt $stream.Length - 8) {
                throw "Module '$($module.FullName)' has a truncated certificate directory."
            }
            $stream.Position = $certificateDirectoryOffset
            [UInt32]$certificateOffset = $reader.ReadUInt32()
            [UInt32]$certificateSize = $reader.ReadUInt32()
            if ($certificateOffset -eq 0 -and $certificateSize -eq 0) {
                continue
            }
            [UInt64]$certificateEnd =
                [UInt64]$certificateOffset + [UInt64]$certificateSize
            if ($certificateOffset -eq 0 -or
                $certificateSize -lt 8 -or
                $certificateOffset -lt $certificateDirectoryOffset + 8 -or
                $certificateEnd -ne [UInt64]$stream.Length) {
                throw "Module '$($module.FullName)' does not have one terminal PE certificate table."
            }

            $stream.Position = $certificateDirectoryOffset
            $stream.Write((New-Object byte[] 8), 0, 8)
            $stream.SetLength([Int64]$certificateOffset)
            $stream.Flush($true)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $removedCount++
}

Write-Output "Removed Authenticode certificate tables from $removedCount module(s)."
