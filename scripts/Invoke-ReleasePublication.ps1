[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Publish', 'Verify')]
    [string] $Mode,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')]
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $GitHubApiUrl,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('\Arelease-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string] $ReleaseTag,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[0-9a-f]{40}\z')]
    [string] $TrustedSha,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[0-9a-f]{40}\z')]
    [string] $TrustedTagOid,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $AssetDirectory,

    [Parameter()]
    [AllowEmptyString()]
    [string] $ReleaseNotesPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maximumReleasePages = 10
$maximumAssetPages = 10
$maximumAssetBytes = 512MB
$maximumAggregateBytes = 2GB
$maximumReleaseNotesBytes = 128KB
$expectedReleaseName = "WireSockUI-$ReleaseTag"

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN is required for release publication or verification.'
}
if ($Repository -cne 'wiresock/WireSockUI') {
    throw "Release publication may run only for canonical repository 'wiresock/WireSockUI', not '$Repository'."
}
if ($ReleaseTag -cne "release-v$Version") {
    throw "Release tag '$ReleaseTag' does not match package version '$Version'."
}

if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $ReleaseNotesPath = Join-Path `
        (Split-Path -Parent $PSScriptRoot) `
        "docs/release-notes/$ReleaseTag.md"
}
$releaseNotesFile = Get-Item `
    -LiteralPath ([IO.Path]::GetFullPath($ReleaseNotesPath)) `
    -Force
$releaseNotesLinkType = $releaseNotesFile.PSObject.Properties['LinkType']
if ($releaseNotesFile.PSIsContainer -or
    ($releaseNotesFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    ($null -ne $releaseNotesLinkType -and
        -not [string]::IsNullOrEmpty([string]$releaseNotesLinkType.Value))) {
    throw "Release notes '$($releaseNotesFile.FullName)' must be an ordinary file."
}
if ([Int64]$releaseNotesFile.Length -lt 1 -or
    [Int64]$releaseNotesFile.Length -gt $maximumReleaseNotesBytes) {
    throw "Release notes must be between 1 and $maximumReleaseNotesBytes bytes."
}
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
try {
    $releaseNotes = [IO.File]::ReadAllText(
        $releaseNotesFile.FullName,
        $strictUtf8)
}
catch [Text.DecoderFallbackException] {
    throw "Release notes '$($releaseNotesFile.FullName)' must contain valid UTF-8."
}
$expectedReleaseBody = ($releaseNotes -replace '\r\n?', "`n").Trim()
if ([string]::IsNullOrWhiteSpace($expectedReleaseBody)) {
    throw 'Release notes must contain non-empty text.'
}
$expectedReleaseHeading = "## WireSock UI $Version"
$releaseHeading = ($expectedReleaseBody -split "`n", 2)[0]
if ($releaseHeading -cne $expectedReleaseHeading) {
    throw "Release notes must start with the exact heading '$expectedReleaseHeading'."
}
if ($expectedReleaseBody -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
    throw 'Release notes contain a disallowed control character.'
}

$apiUri = $null
if (-not [Uri]::TryCreate($GitHubApiUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    $apiUri.Scheme -cne 'https' -or
    $apiUri.DnsSafeHost -cne 'api.github.com' -or
    -not $apiUri.IsDefaultPort -or
    -not [string]::IsNullOrEmpty($apiUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($apiUri.Query) -or
    -not [string]::IsNullOrEmpty($apiUri.Fragment) -or
    $apiUri.AbsolutePath.Trim('/') -ne '') {
    throw 'GitHubApiUrl must be the exact public GitHub HTTPS API origin.'
}
$apiRoot = $GitHubApiUrl.TrimEnd('/')
$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $env:GH_TOKEN"
    'X-GitHub-Api-Version' = '2026-03-10'
}

function Assert-OrdinaryFile {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileInfo] $File
    )

    $linkTypeProperty = $File.PSObject.Properties['LinkType']
    if (($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($null -ne $linkTypeProperty -and
            -not [string]::IsNullOrEmpty([string]$linkTypeProperty.Value))) {
        throw "Release asset '$($File.FullName)' must not be a link or reparse point."
    }
    if ([Int64]$File.Length -lt 1 -or [Int64]$File.Length -gt $maximumAssetBytes) {
        throw "Release asset '$($File.Name)' is empty or exceeds the $maximumAssetBytes-byte limit."
    }
}

function Get-ExpectedAssetNames {
    $names = [Collections.Generic.List[string]]::new()
    foreach ($architecture in @('x86', 'x64', 'ARM64')) {
        foreach ($flavor in @('no-uwp', 'uwp')) {
            $msiArchitecture = $architecture.ToLowerInvariant()
            $msi = "WireSockUI-$Version-win-$msiArchitecture-$flavor.msi"
            $validation = "$msi.validation.json"
            $sbom = "WireSockUI-v$Version-$architecture-$flavor.spdx.json"
            foreach ($asset in @($msi, $validation, $sbom)) {
                $names.Add($asset)
                $names.Add("$asset.sha256")
            }
        }
    }
    return @($names | Sort-Object)
}

function Get-LocalAssetInventory {
    $root = (Resolve-Path -LiteralPath $AssetDirectory).Path
    $rootItem = Get-Item -LiteralPath $root -Force
    $rootLinkType = $rootItem.PSObject.Properties['LinkType']
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($null -ne $rootLinkType -and
            -not [string]::IsNullOrEmpty([string]$rootLinkType.Value))) {
        throw "Release asset root '$root' must be an ordinary directory."
    }

    $children = @(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop)
    if (@($children | Where-Object { $_.PSIsContainer }).Count -ne 0) {
        throw 'Release asset root must contain files directly and no subdirectories.'
    }
    $files = @($children | Where-Object { -not $_.PSIsContainer })
    $expectedNames = @(Get-ExpectedAssetNames)
    $actualNames = @($files.Name | Sort-Object)
    $differences = @(
        Compare-Object `
            -ReferenceObject $expectedNames `
            -DifferenceObject $actualNames `
            -CaseSensitive
    )
    if ($differences.Count -ne 0 -or $files.Count -ne 36) {
        throw 'Local release assets are not the exact expected 36-file architecture/flavor set.'
    }

    $caseInsensitiveNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $inventory = [Collections.Generic.List[object]]::new()
    [Int64]$aggregateBytes = 0
    foreach ($file in $files) {
        Assert-OrdinaryFile -File $file
        if (-not $caseInsensitiveNames.Add($file.Name)) {
            throw "Local release assets contain case-insensitive name collision '$($file.Name)'."
        }
        if ([Int64]$file.Length -gt ($maximumAggregateBytes - $aggregateBytes)) {
            throw "Local release assets exceed the $maximumAggregateBytes-byte aggregate limit."
        }
        $aggregateBytes += [Int64]$file.Length
        $inventory.Add([pscustomobject]@{
                Name = $file.Name
                FullName = $file.FullName
                Length = [Int64]$file.Length
                Sha256 = (
                    Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName
                ).Hash.ToLowerInvariant()
                Attested = -not $file.Name.EndsWith(
                    '.sha256',
                    [StringComparison]::Ordinal)
            })
    }

    $byName = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($asset in $inventory) {
        $byName.Add($asset.Name, $asset)
    }
    foreach ($asset in @($inventory | Where-Object { $_.Attested })) {
        $sidecar = $byName["$($asset.Name).sha256"]
        if ([Int64]$sidecar.Length -gt 512) {
            throw "Checksum sidecar '$($sidecar.Name)' exceeds the 512-byte limit."
        }
        $checksumText = [IO.File]::ReadAllText(
            $sidecar.FullName,
            [Text.Encoding]::ASCII)
        $expectedPattern = (
            '^' +
            [Regex]::Escape($asset.Sha256) +
            '  ' +
            [Regex]::Escape($asset.Name) +
            '\r?\n?$')
        if ($checksumText -cnotmatch $expectedPattern) {
            throw "Checksum sidecar '$($sidecar.Name)' is not the exact SHA-256 record for '$($asset.Name)'."
        }
    }

    return @($inventory | Sort-Object -Property Name)
}

function Invoke-GitHubRequest {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Get', 'Post', 'Patch', 'Delete')]
        [string] $Method,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $RelativePath,

        [Parameter()]
        [object] $Body
    )

    if ($RelativePath.StartsWith('/') -or
        $RelativePath.Contains('://') -or
        $RelativePath.IndexOfAny([char[]]"`r`n") -ge 0) {
        throw "Unsafe GitHub API relative path '$RelativePath'."
    }
    $parameters = @{
        Method = $Method
        Headers = $headers
        Uri = "$apiRoot/repos/$Repository/$RelativePath"
        TimeoutSec = 30
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 6 -Compress
    }
    return Invoke-RestMethod @parameters
}

function Get-AllReleases {
    $releases = [Collections.Generic.List[object]]::new()
    foreach ($page in 1..$maximumReleasePages) {
        # Assign before array-wrapping. Invoke-RestMethod emits JSON arrays as a
        # single pipeline object, and calling the wrapper directly inside @()
        # preserves that nested array instead of enumerating its items.
        $pageResponse =
            Invoke-GitHubRequest `
                -Method Get `
                -RelativePath "releases?per_page=100&page=$page"
        $pageItems = @($pageResponse)
        foreach ($item in $pageItems) {
            $releases.Add($item)
        }
        if ($pageItems.Count -lt 100) {
            return $releases.ToArray()
        }
    }
    throw "Repository release inventory exceeds the bounded $($maximumReleasePages * 100)-release scan."
}

function Get-AllReleaseAssets {
    param(
        [Parameter(Mandatory = $true)]
        [Int64] $ReleaseId
    )

    $assets = [Collections.Generic.List[object]]::new()
    foreach ($page in 1..$maximumAssetPages) {
        $pageResponse =
            Invoke-GitHubRequest `
                -Method Get `
                -RelativePath "releases/$ReleaseId/assets?per_page=100&page=$page"
        $pageItems = @($pageResponse)
        foreach ($item in $pageItems) {
            $assets.Add($item)
        }
        if ($pageItems.Count -lt 100) {
            return $assets.ToArray()
        }
    }
    throw "Remote release asset inventory exceeds the bounded $($maximumAssetPages * 100)-asset scan."
}

function Assert-ReleaseIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Release,

        [Parameter(Mandatory = $true)]
        [bool] $RequirePublished
    )

    [Int64]$releaseId = $Release.id
    if ($releaseId -le 0 -or
        [string]$Release.tag_name -cne $ReleaseTag -or
        [string]$Release.target_commitish -cne $TrustedSha -or
        [string]$Release.name -cne $expectedReleaseName -or
        [string]$Release.body -cne $expectedReleaseBody -or
        $Release.prerelease -ne $false) {
        throw "GitHub release for '$ReleaseTag' has unexpected identity, target, title, body, or prerelease state."
    }

    $expectedApiUrl = "$apiRoot/repos/$Repository/releases/$releaseId"
    $expectedUploadUrl = (
        "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets{?name,label}")
    if ([string]$Release.url -cne $expectedApiUrl -or
        [string]$Release.upload_url -cne $expectedUploadUrl) {
        throw "GitHub release '$releaseId' returned an unexpected API or upload URL."
    }

    if ($RequirePublished) {
        if ($Release.draft -ne $false -or
            $Release.immutable -ne $true -or
            [string]::IsNullOrWhiteSpace([string]$Release.published_at)) {
            throw "GitHub release '$ReleaseTag' is not published and immutable."
        }
    }
    elseif ($Release.draft -ne $true -or $Release.immutable -ne $false) {
        throw "Resumable GitHub release '$ReleaseTag' must be a mutable draft."
    }
}

function Test-RemoteAssetInventory {
    param(
        [Parameter(Mandatory = $true)]
        [Int64] $ReleaseId,

        [Parameter(Mandatory = $true)]
        [object[]] $LocalAssets,

        [Parameter(Mandatory = $true)]
        [bool] $AllowPartial
    )

    $localByName = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($localAsset in $LocalAssets) {
        $localByName.Add($localAsset.Name, $localAsset)
    }

    $remoteAssets = @(Get-AllReleaseAssets -ReleaseId $ReleaseId)
    $remoteNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $uploadedNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $staleAssets = [Collections.Generic.List[object]]::new()
    foreach ($remoteAsset in $remoteAssets) {
        $name = [string]$remoteAsset.name
        if ([string]::IsNullOrWhiteSpace($name) -or
            -not $remoteNames.Add($name) -or
            -not $localByName.ContainsKey($name)) {
            throw "Remote release contains unexpected or duplicate asset '$name'."
        }
        [Int64]$assetId = $remoteAsset.id
        if ($assetId -le 0) {
            throw "Remote release asset '$name' has an invalid identifier."
        }

        $expected = $localByName[$name]
        $state = [string]$remoteAsset.state
        if ($state -ceq 'starter' -and
            $AllowPartial -and
            [Int64]$remoteAsset.size -eq 0 -and
            [string]::IsNullOrEmpty([string]$remoteAsset.digest)) {
            $staleAssets.Add($remoteAsset)
            continue
        }
        if ($state -cne 'uploaded' -or
            [Int64]$remoteAsset.size -ne [Int64]$expected.Length -or
            [string]$remoteAsset.digest -cne "sha256:$($expected.Sha256)") {
            throw "Remote release asset '$name' does not match the exact local size and SHA-256 digest."
        }
        [void]$uploadedNames.Add($name)
    }

    $missing = @(
        $LocalAssets |
            Where-Object { -not $uploadedNames.Contains($_.Name) } |
            Sort-Object -Property Name
    )
    if (-not $AllowPartial -and
        ($missing.Count -ne 0 -or $remoteAssets.Count -ne $LocalAssets.Count)) {
        throw 'Published release does not contain the exact expected remote asset set.'
    }
    return [pscustomobject]@{
        Missing = $missing
        Stale = @($staleAssets)
    }
}

function Assert-TrustedReleaseRef {
    $null = & (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') `
        -Repository $Repository `
        -GitHubApiUrl $GitHubApiUrl `
        -ReleaseTag $ReleaseTag `
        -TrustedSha $TrustedSha `
        -TrustedTagOid $TrustedTagOid
}

function Get-MatchingRelease {
    $matching = @(
        Get-AllReleases |
            Where-Object { [string]$_.tag_name -ceq $ReleaseTag }
    )
    if ($matching.Count -gt 1) {
        throw "Repository contains duplicate release records for '$ReleaseTag'."
    }
    if ($matching.Count -eq 0) {
        return $null
    }
    return $matching[0]
}

$localAssets = @(Get-LocalAssetInventory)
Assert-TrustedReleaseRef
$release = Get-MatchingRelease

if ($Mode -ceq 'Publish') {
    if ($null -eq $release) {
        $release = Invoke-GitHubRequest `
            -Method Post `
            -RelativePath 'releases' `
            -Body ([ordered]@{
                    tag_name = $ReleaseTag
                    target_commitish = $TrustedSha
                    name = $expectedReleaseName
                    body = $expectedReleaseBody
                    draft = $true
                    prerelease = $false
                    generate_release_notes = $false
                    make_latest = 'true'
                })
    }

    if ($release.draft -eq $false) {
        Assert-ReleaseIdentity -Release $release -RequirePublished $true
        [void](Test-RemoteAssetInventory `
                -ReleaseId ([Int64]$release.id) `
                -LocalAssets $localAssets `
                -AllowPartial $false)
        Assert-TrustedReleaseRef
        Write-Output "Release '$ReleaseTag' was already published with the exact immutable asset set."
        return
    }

    Assert-ReleaseIdentity -Release $release -RequirePublished $false
    [Int64]$releaseId = $release.id
    $partial = Test-RemoteAssetInventory `
        -ReleaseId $releaseId `
        -LocalAssets $localAssets `
        -AllowPartial $true

    foreach ($staleAsset in @($partial.Stale)) {
        $null = Invoke-GitHubRequest `
            -Method Delete `
            -RelativePath "releases/assets/$([Int64]$staleAsset.id)"
    }
    if (@($partial.Stale).Count -gt 0) {
        $partial = Test-RemoteAssetInventory `
            -ReleaseId $releaseId `
            -LocalAssets $localAssets `
            -AllowPartial $true
    }

    foreach ($asset in @($partial.Missing)) {
        $encodedName = [Uri]::EscapeDataString($asset.Name)
        $uploadUri = (
            "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets?name=$encodedName")
        $uploaded = $false
        foreach ($attempt in 1..3) {
            try {
                $uploadResult = Invoke-RestMethod `
                    -Method Post `
                    -Headers $headers `
                    -Uri $uploadUri `
                    -ContentType 'application/octet-stream' `
                    -TimeoutSec 300 `
                    -InFile $asset.FullName
                if ([string]$uploadResult.name -cne $asset.Name) {
                    throw "GitHub returned the wrong asset after uploading '$($asset.Name)'."
                }
                $uploaded = $true
                break
            }
            catch {
                $statusCode = 0
                if ($null -ne $_.Exception.Response) {
                    $statusCode = [int]$_.Exception.Response.StatusCode
                }
                $current = Test-RemoteAssetInventory `
                    -ReleaseId $releaseId `
                    -LocalAssets $localAssets `
                    -AllowPartial $true
                if (@($current.Missing | Where-Object {
                            $_.Name -ceq $asset.Name
                        }).Count -eq 0) {
                    $uploaded = $true
                    break
                }
                $newStarters = @(
                    $current.Stale |
                        Where-Object { [string]$_.name -ceq $asset.Name }
                )
                foreach ($starter in $newStarters) {
                    $null = Invoke-GitHubRequest `
                        -Method Delete `
                        -RelativePath "releases/assets/$([Int64]$starter.id)"
                }
                if ($attempt -eq 3 -or
                    $statusCode -notin @(408, 422, 429, 500, 502, 503, 504)) {
                    throw
                }
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
        if (-not $uploaded) {
            throw "Unable to upload release asset '$($asset.Name)' after three bounded attempts."
        }
    }

    $complete = $false
    foreach ($attempt in 1..6) {
        try {
            [void](Test-RemoteAssetInventory `
                    -ReleaseId $releaseId `
                    -LocalAssets $localAssets `
                    -AllowPartial $false)
            $complete = $true
            break
        }
        catch {
            if ($attempt -eq 6) {
                throw
            }
            Start-Sleep -Seconds 2
        }
    }
    if (-not $complete) {
        throw 'Remote release asset inventory did not stabilize.'
    }

    Assert-TrustedReleaseRef
    $release = Invoke-GitHubRequest `
        -Method Patch `
        -RelativePath "releases/$releaseId" `
        -Body ([ordered]@{
                tag_name = $ReleaseTag
                target_commitish = $TrustedSha
                name = $expectedReleaseName
                body = $expectedReleaseBody
                draft = $false
                prerelease = $false
                make_latest = 'true'
            })

    $published = $false
    foreach ($attempt in 1..6) {
        try {
            $release = Invoke-GitHubRequest `
                -Method Get `
                -RelativePath "releases/$releaseId"
            Assert-ReleaseIdentity -Release $release -RequirePublished $true
            [void](Test-RemoteAssetInventory `
                    -ReleaseId $releaseId `
                    -LocalAssets $localAssets `
                    -AllowPartial $false)
            $published = $true
            break
        }
        catch {
            if ($attempt -eq 6) {
                throw
            }
            Start-Sleep -Seconds 5
        }
    }
    if (-not $published) {
        throw "Release '$ReleaseTag' did not become immutable after publication."
    }
    Assert-TrustedReleaseRef
    Write-Output "Published immutable release '$ReleaseTag' with the exact 36-file asset set."
    return
}

if ($null -eq $release) {
    throw "Published release '$ReleaseTag' does not exist."
}
Assert-ReleaseIdentity -Release $release -RequirePublished $true
[void](Test-RemoteAssetInventory `
        -ReleaseId ([Int64]$release.id) `
        -LocalAssets $localAssets `
        -AllowPartial $false)
Assert-TrustedReleaseRef

$ghVersionOutput = @(& gh --version 2>&1)
if ($LASTEXITCODE -ne 0 -or
    $ghVersionOutput.Count -lt 1 -or
    $ghVersionOutput[0] -cnotmatch '^gh version (?<version>[0-9]+\.[0-9]+\.[0-9]+)(?: |$)') {
    throw 'Unable to resolve the installed GitHub CLI version.'
}
$ghVersion = [Version]$Matches.version
if ($ghVersion -lt [Version]'2.93.0') {
    throw "GitHub CLI $ghVersion is vulnerable or lacks required immutable-release verification; version 2.93.0 or newer is required."
}

$releaseVerified = $false
foreach ($attempt in 1..6) {
    & gh release verify $ReleaseTag --repo $Repository --format json | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $releaseVerified = $true
        break
    }
    if ($attempt -lt 6) {
        Start-Sleep -Seconds 5
    }
}
if (-not $releaseVerified) {
    throw 'Immutable GitHub release attestation verification did not succeed after six bounded attempts.'
}

foreach ($asset in $localAssets) {
    & gh release verify-asset $ReleaseTag $asset.FullName --repo $Repository --format json |
        Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Immutable release asset verification failed for '$($asset.Name)'."
    }
    if (-not $asset.Attested) {
        continue
    }

    & gh attestation verify $asset.FullName `
        --repo $Repository `
        --signer-workflow "$Repository/.github/workflows/main.yml" `
        --signer-digest $TrustedSha `
        --source-digest $TrustedSha `
        --source-ref "refs/tags/$ReleaseTag" `
        --deny-self-hosted-runners `
        --predicate-type 'https://slsa.dev/provenance/v1' `
        --format json |
        Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Repository provenance attestation verification failed for '$($asset.Name)'."
    }
}

Assert-TrustedReleaseRef
Write-Output "Verified immutable release, exact remote digests, and trusted hosted-runner provenance for '$ReleaseTag'."
