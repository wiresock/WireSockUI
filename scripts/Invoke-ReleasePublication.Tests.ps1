[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptUnderTest = Join-Path $PSScriptRoot 'Invoke-ReleasePublication.ps1'
$testRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ("WireSockUI-Publication-{0}" -f [Guid]::NewGuid().ToString('N'))
$assetRoot = Join-Path $testRoot 'assets'
[void](New-Item -ItemType Directory -Path $assetRoot -Force)
$releaseNotesPath = Join-Path $testRoot 'release-notes.md'
$expectedTestReleaseBody = (
    "## WireSock UI 1.2.3`n`n" +
    'Human-friendly fixture release notes.')
[IO.File]::WriteAllText(
    $releaseNotesPath,
    ($expectedTestReleaseBody -replace "`n", "`r`n") + "`r`n",
    [Text.UTF8Encoding]::new($false))

$global:PublicationTestTagOid = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$global:PublicationTestSha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
$global:PublicationTestMainSha = $global:PublicationTestSha
$global:PublicationTestReleaseId = [Int64]123
$global:PublicationTestExpectedAssets = @{}
$global:PublicationTestRemoteAssets = @()
$global:PublicationTestRelease = $null
$global:PublicationTestGhCalls = [Collections.Generic.List[string]]::new()
$global:PublicationTestFailUploadName = $null
$global:PublicationTestFailedUploadOnce = $false
$global:PublicationTestCreateRequest = $null

function Write-TestAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    $path = Join-Path $assetRoot $Name
    [IO.File]::WriteAllText($path, $Value, [Text.Encoding]::UTF8)
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    $sidecarPath = "$path.sha256"
    [IO.File]::WriteAllText(
        $sidecarPath,
        "$hash  $Name`r`n",
        [Text.Encoding]::ASCII)
}

foreach ($architecture in @('x86', 'x64', 'ARM64')) {
    foreach ($flavor in @('no-uwp', 'uwp')) {
        $msiArchitecture = $architecture.ToLowerInvariant()
        $msi = "WireSockUI-1.2.3-win-$msiArchitecture-$flavor.msi"
        Write-TestAsset -Name $msi -Value "$architecture-$flavor-msi"
        Write-TestAsset -Name "$msi.validation.json" -Value (
            "{`"architecture`":`"$architecture`",`"flavor`":`"$flavor`"}")
        Write-TestAsset `
            -Name "WireSockUI-v1.2.3-$architecture-$flavor.spdx.json" `
            -Value "{`"name`":`"$architecture-$flavor`"}"
    }
}

foreach ($file in Get-ChildItem -LiteralPath $assetRoot -File) {
    $global:PublicationTestExpectedAssets[$file.Name] = [pscustomobject]@{
        Name = $file.Name
        FullName = $file.FullName
        Size = [Int64]$file.Length
        Digest = (
            Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName
        ).Hash.ToLowerInvariant()
    }
}

function New-TestRemoteAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [Int64] $Id
    )

    $expected = $global:PublicationTestExpectedAssets[$Name]
    return [pscustomobject]@{
        id = $Id
        name = $Name
        state = 'uploaded'
        size = $expected.Size
        digest = "sha256:$($expected.Digest)"
    }
}

function Reset-TestRelease {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $Draft,

        [Parameter(Mandatory = $true)]
        [int] $RemoteAssetCount
    )

    $global:PublicationTestRelease = [pscustomobject]@{
        id = $global:PublicationTestReleaseId
        tag_name = 'release-v1.2.3'
        target_commitish = $global:PublicationTestSha
        name = 'WireSockUI-release-v1.2.3'
        body = $expectedTestReleaseBody
        draft = $Draft
        prerelease = $false
        immutable = -not $Draft
        published_at = if ($Draft) { $null } else { '2026-07-28T12:00:00Z' }
        url = (
            'https://api.github.com/repos/wiresock/WireSockUI/releases/' +
            $global:PublicationTestReleaseId)
        upload_url = (
            'https://uploads.github.com/repos/wiresock/WireSockUI/releases/' +
            $global:PublicationTestReleaseId +
            '/assets{?name,label}')
    }
    $assetNames = @($global:PublicationTestExpectedAssets.Keys | Sort-Object)
    $remote = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $RemoteAssetCount; $index++) {
        $remote.Add((New-TestRemoteAsset `
                    -Name $assetNames[$index] `
                    -Id ([Int64](1000 + $index))))
    }
    $global:PublicationTestRemoteAssets = @($remote)
}

function global:Invoke-RestMethod {
    param(
        [string] $Method = 'Get',
        [hashtable] $Headers,
        [string] $Uri,
        [string] $ContentType,
        [int] $TimeoutSec,
        [string] $Body,
        [string] $InFile
    )

    if ($Uri -eq 'https://api.github.com/repos/wiresock/WireSockUI/git/ref/tags/release-v1.2.3') {
        return [pscustomobject]@{
            ref = 'refs/tags/release-v1.2.3'
            object = [pscustomobject]@{
                type = 'tag'
                sha = $global:PublicationTestTagOid
            }
        }
    }
    if ($Uri -eq "https://api.github.com/repos/wiresock/WireSockUI/git/tags/$global:PublicationTestTagOid") {
        return [pscustomobject]@{
            sha = $global:PublicationTestTagOid
            tag = 'release-v1.2.3'
            verification = [pscustomobject]@{ verified = $true }
            object = [pscustomobject]@{
                type = 'commit'
                sha = $global:PublicationTestSha
            }
        }
    }
    if ($Uri -eq 'https://api.github.com/repos/wiresock/WireSockUI/git/ref/heads/main') {
        return [pscustomobject]@{
            ref = 'refs/heads/main'
            object = [pscustomobject]@{
                type = 'commit'
                sha = $global:PublicationTestMainSha
            }
        }
    }
    if ($Uri -eq 'https://api.github.com/repos/wiresock/WireSockUI/releases' -and
        $Method -eq 'Post') {
        $document = $Body | ConvertFrom-Json
        if ([string]$document.tag_name -cne 'release-v1.2.3' -or
            [string]$document.target_commitish -cne $global:PublicationTestSha -or
            [string]$document.name -cne 'WireSockUI-release-v1.2.3' -or
            [string]$document.body -cne $expectedTestReleaseBody -or
            $document.draft -ne $true -or
            $document.prerelease -ne $false -or
            $document.generate_release_notes -ne $false -or
            [string]$document.make_latest -cne 'true') {
            throw 'Fixture received an invalid release creation request.'
        }
        $global:PublicationTestCreateRequest = $document
        Reset-TestRelease -Draft $true -RemoteAssetCount 0
        return $global:PublicationTestRelease
    }
    if ($Uri -match '/releases\?per_page=100&page=1$') {
        if ($null -eq $global:PublicationTestRelease) {
            return @()
        }
        Write-Output `
            -NoEnumerate `
            -InputObject @($global:PublicationTestRelease)
        return
    }
    if ($Uri -match '/releases\?per_page=100&page=[2-9][0-9]*$') {
        return @()
    }
    if ($Uri -match "/releases/$global:PublicationTestReleaseId/assets\?per_page=100&page=1$") {
        Write-Output `
            -NoEnumerate `
            -InputObject @($global:PublicationTestRemoteAssets)
        return
    }
    if ($Uri -match "/releases/$global:PublicationTestReleaseId/assets\?per_page=100&page=[2-9][0-9]*$") {
        return @()
    }
    if ($Uri -eq "https://api.github.com/repos/wiresock/WireSockUI/releases/$global:PublicationTestReleaseId" -and
        $Method -eq 'Get') {
        return $global:PublicationTestRelease
    }
    if ($Uri -eq "https://api.github.com/repos/wiresock/WireSockUI/releases/$global:PublicationTestReleaseId" -and
        $Method -eq 'Patch') {
        $document = $Body | ConvertFrom-Json
        if ($document.draft -ne $false) {
            throw 'Fixture received an invalid release publication request.'
        }
        $global:PublicationTestRelease.draft = $false
        $global:PublicationTestRelease.immutable = $true
        $global:PublicationTestRelease.published_at = '2026-07-28T12:00:00Z'
        return $global:PublicationTestRelease
    }
    if ($Uri -match '/releases/assets/(?<id>[0-9]+)$' -and $Method -eq 'Delete') {
        [Int64]$assetId = $Matches.id
        $global:PublicationTestRemoteAssets = @(
            $global:PublicationTestRemoteAssets |
                Where-Object { [Int64]$_.id -ne $assetId }
        )
        return $null
    }
    if ($Uri -match '^https://uploads\.github\.com/repos/wiresock/WireSockUI/releases/123/assets\?name=(?<name>.+)$' -and
        $Method -eq 'Post') {
        $name = [Uri]::UnescapeDataString($Matches.name)
        $expected = $global:PublicationTestExpectedAssets[$name]
        if ($null -eq $expected -or $expected.FullName -cne $InFile) {
            throw "Fixture received unexpected upload '$name'."
        }
        if ($name -ceq $global:PublicationTestFailUploadName -and
            -not $global:PublicationTestFailedUploadOnce) {
            $global:PublicationTestFailedUploadOnce = $true
            $global:PublicationTestRemoteAssets += [pscustomobject]@{
                id = [Int64]9999
                name = $name
                state = 'starter'
                size = [Int64]0
                digest = $null
            }
            $response = [Net.Http.HttpResponseMessage]::new(
                [Net.HttpStatusCode]::BadGateway)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new(
                'Fixture transient upload failure.',
                $response)
        }
        $asset = New-TestRemoteAsset `
            -Name $name `
            -Id ([Int64](2000 + $global:PublicationTestRemoteAssets.Count))
        $global:PublicationTestRemoteAssets += $asset
        return $asset
    }
    throw "Unexpected fixture REST request: $Method $Uri"
}

function global:gh {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]] $Arguments
    )

    $global:LASTEXITCODE = 0
    $global:PublicationTestGhCalls.Add(($Arguments -join ' '))
    if ($Arguments.Count -gt 0 -and [string]$Arguments[0] -ceq '--version') {
        Write-Output 'gh version 2.93.0 (fixture)'
        return
    }
    Write-Output '{}'
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Action,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedMessage
    )

    try {
        & $Action
        throw "Expected failure containing '$ExpectedMessage', but the command succeeded."
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected failure containing '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
    }
}

$commonArguments = @{
    Repository = 'wiresock/WireSockUI'
    GitHubApiUrl = 'https://api.github.com'
    ReleaseTag = 'release-v1.2.3'
    Version = '1.2.3'
    TrustedSha = $global:PublicationTestSha
    TrustedTagOid = $global:PublicationTestTagOid
    AssetDirectory = $assetRoot
    ReleaseNotesPath = $releaseNotesPath
}
$env:GH_TOKEN = 'fixture-token'

try {
    $invalidNotesPath = Join-Path $testRoot 'wrong-version.md'
    [IO.File]::WriteAllText(
        $invalidNotesPath,
        "## WireSock UI 1.2.2`n",
        [Text.UTF8Encoding]::new($false))
    $invalidArguments = $commonArguments.Clone()
    $invalidArguments.ReleaseNotesPath = $invalidNotesPath
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @invalidArguments
        } `
        -ExpectedMessage "exact heading '## WireSock UI 1.2.3'"

    $invalidNotesPath = Join-Path $testRoot 'control-character.md'
    [IO.File]::WriteAllText(
        $invalidNotesPath,
        "## WireSock UI 1.2.3`n$([char]27)misleading text",
        [Text.UTF8Encoding]::new($false))
    $invalidArguments.ReleaseNotesPath = $invalidNotesPath
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @invalidArguments
        } `
        -ExpectedMessage 'disallowed control character'

    $invalidNotesPath = Join-Path $testRoot 'invalid-utf8.md'
    [IO.File]::WriteAllBytes(
        $invalidNotesPath,
        [byte[]]@(0xC3, 0x28))
    $invalidArguments.ReleaseNotesPath = $invalidNotesPath
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @invalidArguments
        } `
        -ExpectedMessage 'must contain valid UTF-8'

    $invalidNotesPath = Join-Path $testRoot 'empty.md'
    [IO.File]::WriteAllBytes($invalidNotesPath, [byte[]]@())
    $invalidArguments.ReleaseNotesPath = $invalidNotesPath
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @invalidArguments
        } `
        -ExpectedMessage 'must be between 1 and 131072 bytes'

    $invalidNotesPath = Join-Path $testRoot 'oversized.md'
    [IO.File]::WriteAllBytes(
        $invalidNotesPath,
        [byte[]]::new(131073))
    $invalidArguments.ReleaseNotesPath = $invalidNotesPath
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @invalidArguments
        } `
        -ExpectedMessage 'must be between 1 and 131072 bytes'

    $global:PublicationTestRelease = $null
    $global:PublicationTestRemoteAssets = @()
    $global:PublicationTestCreateRequest = $null
    $null = & $scriptUnderTest -Mode Publish @commonArguments
    if ($null -eq $global:PublicationTestCreateRequest -or
        $global:PublicationTestRelease.draft -ne $false -or
        $global:PublicationTestRelease.immutable -ne $true -or
        $global:PublicationTestRemoteAssets.Count -ne 36) {
        throw 'New release publication did not use the expected notes and exact asset set.'
    }

    Reset-TestRelease -Draft $false -RemoteAssetCount 36
    $global:PublicationTestGhCalls.Clear()
    $null = & $scriptUnderTest -Mode Verify @commonArguments
    if ($global:PublicationTestGhCalls.Count -ne 56) {
        throw "Expected 56 GitHub CLI verification calls, found $($global:PublicationTestGhCalls.Count)."
    }

    $global:PublicationTestRelease.body = 'Unexpected release notes.'
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @commonArguments
        } `
        -ExpectedMessage 'unexpected identity'

    Reset-TestRelease -Draft $false -RemoteAssetCount 36

    $global:PublicationTestMainSha = 'cccccccccccccccccccccccccccccccccccccccc'
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @commonArguments
        } `
        -ExpectedMessage 'Protected branch'
    $global:PublicationTestMainSha = $global:PublicationTestSha

    $global:PublicationTestRemoteAssets[0].digest = 'sha256:' + ('0' * 64)
    Assert-Throws `
        -Action {
            & $scriptUnderTest -Mode Verify @commonArguments
        } `
        -ExpectedMessage 'exact local size and SHA-256 digest'

    Reset-TestRelease -Draft $false -RemoteAssetCount 36
    $null = & $scriptUnderTest -Mode Publish @commonArguments

    Reset-TestRelease -Draft $true -RemoteAssetCount 5
    $assetNames = @($global:PublicationTestExpectedAssets.Keys | Sort-Object)
    $global:PublicationTestFailUploadName = $assetNames[5]
    $global:PublicationTestFailedUploadOnce = $false
    $null = & $scriptUnderTest -Mode Publish @commonArguments
    if ($global:PublicationTestRelease.draft -ne $false -or
        $global:PublicationTestRelease.immutable -ne $true -or
        $global:PublicationTestRemoteAssets.Count -ne 36 -or
        -not $global:PublicationTestFailedUploadOnce -or
        @($global:PublicationTestRemoteAssets | Where-Object {
                [string]$_.state -ceq 'starter'
            }).Count -ne 0) {
        throw 'Draft resume did not upload the missing exact asset set and publish immutably.'
    }

    Write-Output 'Release publication tests passed.'
}
finally {
    Remove-Item -LiteralPath Function:\Invoke-RestMethod -Force
    Remove-Item -LiteralPath Function:\gh -Force
    Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
    foreach ($name in @(
            'PublicationTestTagOid',
            'PublicationTestSha',
            'PublicationTestMainSha',
            'PublicationTestReleaseId',
            'PublicationTestExpectedAssets',
            'PublicationTestRemoteAssets',
            'PublicationTestRelease',
            'PublicationTestCreateRequest',
            'PublicationTestGhCalls',
            'PublicationTestFailUploadName',
            'PublicationTestFailedUploadOnce')) {
        Remove-Variable -Name $name -Scope Global -ErrorAction SilentlyContinue
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTestRoot.StartsWith(
            $resolvedTempRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot) -match '^WireSockUI-Publication-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
