#requires -Version 7.0

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptUnderTest = Join-Path $PSScriptRoot 'Resolve-SdkReleaseBranch.ps1'
$global:SdkReleaseFixtureCalls = [Collections.Generic.List[string]]::new()
$global:SdkReleaseFixtureRepeatFullPage = $false
$global:SdkReleaseFixtureMetadata = $null
$global:SdkReleaseFixturePages = @{}

function New-FixtureSha {
    param(
        [Parameter(Mandatory = $true)]
        [int] $Page,

        [Parameter(Mandatory = $true)]
        [int] $Index
    )

    return ('{0:x2}{1:x2}{2}' -f $Page, $Index, ('ab' * 18))
}

function New-FixtureBranch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Sha
    )

    return [pscustomobject]@{
        name = $Name
        commit = [pscustomobject]@{
            sha = $Sha
        }
    }
}

function New-CanonicalMetadata {
    return [pscustomobject]@{
        full_name = 'Wiresock-Foundation/wiresock-vpn-client'
        owner = [pscustomobject]@{
            login = 'Wiresock-Foundation'
            type = 'Organization'
        }
    }
}

function Reset-SdkReleaseFixture {
    $global:SdkReleaseFixtureCalls = [Collections.Generic.List[string]]::new()
    $global:SdkReleaseFixtureRepeatFullPage = $false
    $global:SdkReleaseFixtureMetadata = New-CanonicalMetadata
    $global:SdkReleaseFixturePages = @{
        1 = @(
            (New-FixtureBranch -Name 'main' -Sha (New-FixtureSha -Page 1 -Index 7)),
            (New-FixtureBranch -Name 'release/v2.4.28' -Sha (New-FixtureSha -Page 1 -Index 1)),
            (New-FixtureBranch -Name 'release/v3.8.1' -Sha (New-FixtureSha -Page 1 -Index 2)),
            (New-FixtureBranch -Name 'release/v3.8.2' -Sha (New-FixtureSha -Page 1 -Index 3)),
            (New-FixtureBranch -Name 'release/v3.8.2-beta' -Sha (New-FixtureSha -Page 1 -Index 4)),
            (New-FixtureBranch -Name 'release/v3.8' -Sha (New-FixtureSha -Page 1 -Index 5)),
            (New-FixtureBranch -Name 'release/v03.8.2' -Sha (New-FixtureSha -Page 1 -Index 6))
        )
    }
}

function global:Invoke-RestMethod {
    param(
        [string] $Method,
        [hashtable] $Headers,
        [int] $TimeoutSec,
        [string] $Uri
    )

    if ($Method -cne 'Get' -or $TimeoutSec -ne 30) {
        throw "Unexpected fixture REST method '$Method' or timeout $TimeoutSec."
    }
    if ($Headers.Authorization -cne "Bearer $env:GH_TOKEN" -or
        $Headers.Accept -cne 'application/vnd.github+json' -or
        $Headers['X-GitHub-Api-Version'] -cne '2026-03-10') {
        throw 'Fixture REST request did not use the canonical GitHub API headers.'
    }
    $global:SdkReleaseFixtureCalls.Add($Uri)

    if ($Uri -eq 'https://api.github.com/repos/Wiresock-Foundation/wiresock-vpn-client') {
        return $global:SdkReleaseFixtureMetadata
    }

    $pageMatch = [regex]::Match(
        $Uri,
        ('\Ahttps://api\.github\.com/repos/Wiresock-Foundation/' +
            'wiresock-vpn-client/branches' +
            '\?per_page=100&page=(?<page>[1-9][0-9]*)\z'))
    if (-not $pageMatch.Success) {
        throw "Unexpected fixture REST request: $Uri"
    }

    $page = [int]$pageMatch.Groups['page'].Value
    if ($global:SdkReleaseFixtureRepeatFullPage) {
        $pageBranches = @(
            1..100 | ForEach-Object {
                New-FixtureBranch `
                    -Name ('release/v1.{0}.{1}' -f $page, $_) `
                    -Sha (New-FixtureSha -Page $page -Index $_)
            })
        # return @(...) enumerates. Invoke-RestMethod emits the JSON array
        # as one pipeline object, and @() around the request does not unwrap it.
        Write-Output -NoEnumerate -InputObject $pageBranches
        return
    }
    if ($global:SdkReleaseFixturePages.ContainsKey($page)) {
        $storedPage = $global:SdkReleaseFixturePages[$page]
        # A stored branch object is emitted as itself so @() still wraps one
        # scalar result. Arrays, including an empty page, stay one pipeline object.
        if ($storedPage -isnot [Array]) {
            return $storedPage
        }
        Write-Output -NoEnumerate -InputObject @($storedPage)
        return
    }
    Write-Output -NoEnumerate -InputObject @()
    return
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
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected failure containing '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
        return
    }

    throw "Expected failure containing '$ExpectedMessage', but the command succeeded."
}

function Invoke-Resolver {
    param(
        [string] $Repository = 'Wiresock-Foundation/wiresock-vpn-client',
        [string] $GitHubApiUrl = 'https://api.github.com'
    )

    $outputPath = Join-Path (
        [IO.Path]::GetTempPath()
    ) ('WireSockUI-SdkRelease-' + [Guid]::NewGuid().ToString('N'))
    try {
        $messages = @(
            & $scriptUnderTest `
                -Repository $Repository `
                -GitHubApiUrl $GitHubApiUrl `
                -OutputPath $outputPath)
        return [pscustomobject]@{
            Messages = $messages
            Output = @(Get-Content -LiteralPath $outputPath)
            Calls = @($global:SdkReleaseFixtureCalls)
        }
    }
    finally {
        Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    }
}

Reset-SdkReleaseFixture
$env:GH_TOKEN = 'fixture-token'

try {
    # Test Assert-Throws without using the helper to assert its own failures.
    $successfulActionRejected = $false
    try {
        Assert-Throws -Action {} -ExpectedMessage 'fixture failure'
    }
    catch {
        if ($_.Exception.Message -cne "Expected failure containing 'fixture failure', but the command succeeded.") {
            throw
        }
        $successfulActionRejected = $true
    }
    if (-not $successfulActionRejected) {
        throw 'Assert-Throws accepted an action that did not throw.'
    }

    $wrongExceptionRejected = $false
    try {
        Assert-Throws `
            -Action { throw 'different fixture failure' } `
            -ExpectedMessage 'expected fixture failure'
    }
    catch {
        if ($_.Exception.Message -cne "Expected failure containing 'expected fixture failure', got 'different fixture failure'.") {
            throw
        }
        $wrongExceptionRejected = $true
    }
    if (-not $wrongExceptionRejected) {
        throw 'Assert-Throws accepted an exception with the wrong message.'
    }

    Assert-Throws `
        -Action { throw 'prefix expected fixture failure suffix' } `
        -ExpectedMessage 'expected fixture failure'

    $resolved = Invoke-Resolver
    $expectedSha = New-FixtureSha -Page 1 -Index 3
    if ($resolved.Output.Count -ne 2 -or
        $resolved.Output[0] -cne 'release_branch=release/v3.8.2' -or
        $resolved.Output[1] -cne "release_sha=$expectedSha") {
        throw "Expected release/v3.8.2 at $expectedSha, got '$($resolved.Output -join ' | ')'."
    }
    if ($resolved.Calls.Count -ne 2) {
        throw "Expected the repository lookup and one reference page, got $($resolved.Calls.Count) calls."
    }

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixturePages[1] = @(
        (New-FixtureBranch -Name 'release/v3.8.2' -Sha (New-FixtureSha -Page 1 -Index 2)),
        (New-FixtureBranch -Name 'release/v3.8.10' -Sha (New-FixtureSha -Page 1 -Index 10)),
        (New-FixtureBranch -Name 'release/v3.10.0' -Sha (New-FixtureSha -Page 1 -Index 11)),
        (New-FixtureBranch -Name 'release/v10.0.0' -Sha (New-FixtureSha -Page 1 -Index 12))
    )
    $resolved = Invoke-Resolver
    if ($resolved.Output[0] -cne 'release_branch=release/v10.0.0' -or
        $resolved.Output[1] -cne ('release_sha=' + (New-FixtureSha -Page 1 -Index 12))) {
        throw "Numeric ordering selected '$($resolved.Output -join ' | ')'."
    }

    Reset-SdkReleaseFixture
    $pageOne = [Collections.Generic.List[object]]::new()
    foreach ($index in 1..99) {
        $pageOne.Add(
            (New-FixtureBranch `
                -Name "release/v1.0.$index" `
                -Sha (New-FixtureSha -Page 1 -Index $index)))
    }
    $pageOne.Add(
        (New-FixtureBranch -Name 'release/v3.8.2' -Sha (New-FixtureSha -Page 1 -Index 100)))
    $global:SdkReleaseFixturePages = @{
        1 = @($pageOne)
        2 = @(
            (New-FixtureBranch -Name 'main' -Sha (New-FixtureSha -Page 2 -Index 5)),
            (New-FixtureBranch -Name 'release/v3.8.9' -Sha (New-FixtureSha -Page 2 -Index 1)),
            (New-FixtureBranch -Name 'release/v3.8.10' -Sha (New-FixtureSha -Page 2 -Index 2)),
            (New-FixtureBranch -Name 'release/v9.0.0-rc1' -Sha (New-FixtureSha -Page 2 -Index 3)),
            (New-FixtureBranch -Name 'release/v3.8' -Sha (New-FixtureSha -Page 2 -Index 4))
        )
    }
    $resolved = Invoke-Resolver
    if ($resolved.Output[0] -cne 'release_branch=release/v3.8.10' -or
        $resolved.Output[1] -cne ('release_sha=' + (New-FixtureSha -Page 2 -Index 2)) -or
        $resolved.Calls.Count -ne 3) {
        throw "Paginated resolution selected '$($resolved.Output -join ' | ')' after $($resolved.Calls.Count) calls."
    }

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixturePages[1] = New-FixtureBranch `
        -Name 'release/v3.8.2' `
        -Sha (New-FixtureSha -Page 1 -Index 3)
    $resolved = Invoke-Resolver
    if ($resolved.Output[0] -cne 'release_branch=release/v3.8.2' -or
        $resolved.Output[1] -cne ('release_sha=' + (New-FixtureSha -Page 1 -Index 3)) -or
        $resolved.Calls.Count -ne 2) {
        throw "A single branch object selected '$($resolved.Output -join ' | ')' after $($resolved.Calls.Count) calls."
    }

    Reset-SdkReleaseFixture
    $fullPage = [Collections.Generic.List[object]]::new()
    foreach ($index in 1..99) {
        $fullPage.Add(
            (New-FixtureBranch `
                -Name "release/v2.0.$index" `
                -Sha (New-FixtureSha -Page 1 -Index $index)))
    }
    $fullPage.Add(
        (New-FixtureBranch -Name 'release/v3.8.2' -Sha (New-FixtureSha -Page 1 -Index 100)))
    $global:SdkReleaseFixturePages = @{
        1 = @($fullPage)
    }
    $resolved = Invoke-Resolver
    if ($resolved.Output[0] -cne 'release_branch=release/v3.8.2' -or
        $resolved.Calls.Count -ne 3) {
        throw "A full page followed by an empty page selected '$($resolved.Output -join ' | ')' after $($resolved.Calls.Count) calls."
    }

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixturePages[1] = @(
        (New-FixtureBranch -Name 'main' -Sha (New-FixtureSha -Page 1 -Index 1)),
        (New-FixtureBranch -Name 'release/v3.8.2-rc1' -Sha (New-FixtureSha -Page 1 -Index 2))
    )
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'no release/vMAJOR.MINOR.PATCH branch'

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixturePages[1] = @(
        (New-FixtureBranch -Name 'release/v3.8.2' -Sha (New-FixtureSha -Page 1 -Index 1)),
        (New-FixtureBranch -Name 'release/v3.8.2' -Sha (New-FixtureSha -Page 1 -Index 2))
    )
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'duplicate SDK release branch'

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixturePages[1] = @(
        [pscustomobject]@{
            name = 'release/v3.8.2'
        }
    )
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'has no commit'

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixturePages[1] = @(
        (New-FixtureBranch -Name 'release/v3.8.2' -Sha ('A' + ('b' * 39)))
    )
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'lowercase commit SHA'

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixtureMetadata.full_name = 'Wiresock-Foundation/other'
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'unexpected repository identity'

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixtureMetadata.owner.type = 'User'
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'unexpected repository identity'

    Reset-SdkReleaseFixture
    $global:SdkReleaseFixtureRepeatFullPage = $true
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'exceeds the bounded 2000-branch limit'

    Reset-SdkReleaseFixture
    Assert-Throws `
        -Action {
            Invoke-Resolver -Repository 'wiresock/WireSockUI'
        } `
        -ExpectedMessage 'restricted to'

    Reset-SdkReleaseFixture
    Assert-Throws `
        -Action {
            Invoke-Resolver -GitHubApiUrl 'https://example.invalid'
        } `
        -ExpectedMessage 'public GitHub HTTPS API origin'

    Reset-SdkReleaseFixture
    Remove-Item Env:\GH_TOKEN
    Assert-Throws `
        -Action { Invoke-Resolver } `
        -ExpectedMessage 'GH_TOKEN is required'
    if ($global:SdkReleaseFixtureCalls.Count -ne 0) {
        throw 'Token validation made a REST call.'
    }

    Write-Output 'SDK release branch resolution tests passed.'
}
finally {
    Remove-Item -LiteralPath Function:\Invoke-RestMethod -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
    Remove-Variable -Name SdkReleaseFixtureCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable -Name SdkReleaseFixtureRepeatFullPage -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable -Name SdkReleaseFixtureMetadata -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable -Name SdkReleaseFixturePages -Scope Global -ErrorAction SilentlyContinue
}
