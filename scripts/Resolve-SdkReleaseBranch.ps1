#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')]
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $GitHubApiUrl,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$canonicalRepository = 'Wiresock-Foundation/wiresock-vpn-client'
$releaseBranchPattern = (
    '\Arelease/v' +
    '(?<major>0|[1-9][0-9]{0,8})\.' +
    '(?<minor>0|[1-9][0-9]{0,8})\.' +
    '(?<patch>0|[1-9][0-9]{0,8})\z')
$maximumPages = 20
$pageSize = 100

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN is required to resolve the WireSock SDK release branch.'
}
if ($Repository -cne $canonicalRepository) {
    throw (
        'SDK release resolution is restricted to ' +
        "'$canonicalRepository', not '$Repository'.")
}

$apiUri = $null
if (-not [Uri]::TryCreate($GitHubApiUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    $apiUri.Scheme -cne 'https' -or
    $apiUri.Host -cne 'api.github.com' -or
    -not $apiUri.IsDefaultPort -or
    -not [string]::IsNullOrEmpty($apiUri.UserInfo) -or
    $apiUri.AbsolutePath -cnotin @('', '/') -or
    -not [string]::IsNullOrEmpty($apiUri.Query) -or
    -not [string]::IsNullOrEmpty($apiUri.Fragment)) {
    throw "GitHubApiUrl must be exactly the public GitHub HTTPS API origin 'https://api.github.com'."
}
$apiRoot = $GitHubApiUrl.TrimEnd('/')
$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $env:GH_TOKEN"
    'X-GitHub-Api-Version' = '2026-03-10'
}

function Invoke-GitHubSdkRequest {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $RelativePath
    )

    if ($RelativePath.Contains('://') -or
        $RelativePath.IndexOfAny([char[]]"`r`n") -ge 0) {
        throw "Unsafe WireSock SDK API path '$RelativePath'."
    }
    $uri = "$apiRoot/repos/$Repository"
    if (-not [string]::IsNullOrEmpty($RelativePath)) {
        $uri += "/$RelativePath"
    }
    try {
        return Invoke-RestMethod `
            -Method Get `
            -Headers $headers `
            -TimeoutSec 30 `
            -Uri $uri
    }
    catch {
        throw "Unable to resolve the WireSock SDK release branch at '$RelativePath': $($_.Exception.Message)"
    }
}

function Get-SdkReleaseCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [object] $BranchObject
    )

    $nameProperty = $BranchObject.PSObject.Properties['name']
    if ($null -eq $nameProperty -or
        [string]::IsNullOrWhiteSpace([string]$nameProperty.Value)) {
        throw 'GitHub returned a WireSock SDK branch without a name.'
    }
    $branchName = [string]$nameProperty.Value
    $match = [regex]::Match($branchName, $releaseBranchPattern)
    if (-not $match.Success) {
        return $null
    }

    $commitProperty = $BranchObject.PSObject.Properties['commit']
    if ($null -eq $commitProperty -or $null -eq $commitProperty.Value) {
        throw "SDK release branch '$branchName' has no commit."
    }
    $shaProperty = $commitProperty.Value.PSObject.Properties['sha']
    $sha = if ($null -eq $shaProperty) { '' } else { [string]$shaProperty.Value }
    if ($sha -cnotmatch '\A[0-9a-f]{40}\z') {
        throw "SDK release branch '$branchName' must point at a lowercase commit SHA."
    }

    return [pscustomobject]@{
        Branch = $branchName
        Sha = $sha
        Version = [Version]::new(
            [int]$match.Groups['major'].Value,
            [int]$match.Groups['minor'].Value,
            [int]$match.Groups['patch'].Value)
    }
}

$repositoryMetadata = Invoke-GitHubSdkRequest -RelativePath ''
$ownerProperty = $repositoryMetadata.PSObject.Properties['owner']
$owner = if ($null -eq $ownerProperty) { $null } else { $ownerProperty.Value }
$ownerLogin = ''
$ownerType = ''
if ($null -ne $owner) {
    $loginProperty = $owner.PSObject.Properties['login']
    $typeProperty = $owner.PSObject.Properties['type']
    if ($null -ne $loginProperty) {
        $ownerLogin = [string]$loginProperty.Value
    }
    if ($null -ne $typeProperty) {
        $ownerType = [string]$typeProperty.Value
    }
}
if ([string]$repositoryMetadata.full_name -cne $canonicalRepository -or
    $ownerLogin -cne 'Wiresock-Foundation' -or
    $ownerType -cne 'Organization') {
    throw "GitHub returned an unexpected repository identity for '$canonicalRepository'."
}

$branches = [Collections.Generic.List[object]]::new()
$completed = $false
foreach ($page in 1..$maximumPages) {
    # Assign the REST result before array-wrapping it. Invoke-RestMethod emits
    # JSON arrays as a single pipeline object, and invoking the wrapper
    # directly inside @() preserves that nested array instead of enumerating
    # its branch objects.
    $pageResponse =
        Invoke-GitHubSdkRequest `
            -RelativePath "branches?per_page=$pageSize&page=$page"
    if ($null -eq $pageResponse) {
        $completed = $true
        break
    }
    $pageBranches = @($pageResponse)
    foreach ($pageBranch in $pageBranches) {
        if ($null -eq $pageBranch) {
            throw 'GitHub returned an empty WireSock SDK branch.'
        }
        $branches.Add($pageBranch)
    }
    if ($pageBranches.Count -lt $pageSize) {
        $completed = $true
        break
    }
}
if (-not $completed) {
    throw (
        'WireSock SDK branch scan exceeds the bounded ' +
        "$($maximumPages * $pageSize)-branch limit.")
}

$selected = $null
$seenBranches = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($branchObject in $branches) {
    $candidate = Get-SdkReleaseCandidate -BranchObject $branchObject
    if ($null -eq $candidate) {
        continue
    }
    if (-not $seenBranches.Add($candidate.Branch)) {
        throw "GitHub returned duplicate SDK release branch '$($candidate.Branch)'."
    }
    if ($null -eq $selected -or $candidate.Version -gt $selected.Version) {
        $selected = $candidate
    }
    elseif ($candidate.Version -eq $selected.Version) {
        throw "GitHub returned two SDK branches for version $($candidate.Version)."
    }
}
if ($null -eq $selected) {
    throw 'WireSock SDK repository has no release/vMAJOR.MINOR.PATCH branch.'
}
if ($selected.Branch -cnotmatch $releaseBranchPattern -or
    $selected.Sha -cnotmatch '\A[0-9a-f]{40}\z') {
    throw 'Resolved SDK release branch failed canonical validation.'
}

$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::AppendAllText(
    $OutputPath,
    "release_branch=$($selected.Branch)`nrelease_sha=$($selected.Sha)`n",
    $utf8)
Write-Output (
    "Resolved WireSock SDK release branch $($selected.Branch) " +
    "at $($selected.Sha).")
