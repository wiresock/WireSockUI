#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $WorkflowDirectory = (
        Join-Path (Split-Path -Parent $PSScriptRoot) '.github\workflows'),

    [Parameter()]
    [switch] $RequireProductionContracts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maximumWorkflowCount = 32
$maximumWorkflowBytes = 1MB
$maximumAggregateBytes = 4MB
$totalUsesCount = 0
$productionWorkflowDigests = @{
    # These are intentionally exact contracts for production control flow. A
    # digest is not an authenticity mechanism; it makes every workflow change
    # explicit and subject to focused trust-boundary review.
    'ci.yml' = 'cd7cce3d8d389af13b4b25ee4b29d6783ee010a4a96dc01c48325144e8b6f115'
    'hosted-sdk-experiment.yml' =
        'bf33b7604f89031f7030f791d2dbaa87df9c16b6ef5e7a6d45eca60eb17d7e79'
    'main.yml' = '72ef02fcc5c7a7247a10b395e6d3240d57e1b725c674e4b4810b1a5d1a82a344'
    'sdk-contract-drift.yml' =
        '0d47460aa8e978157d397d26d40dcae9a6c300457b2695d104ff158e61cad771'
    'sdk-integration-schedule.yml' =
        '08757634d63467c5181120d97ecfe17c71393cb4238f42508e6a694dcf4c2de7'
    'sdk-integration.yml' =
        '64cf1be4a6ffe9779aadabb1fa4c887736e3cd55940291aea92da32c0771b849'
    'unsigned-release-candidate.yml' =
        '983f10fdc4e6bd2cdb6e6a7eb79922331b254cc9871083f6fda12dd8d135bdc2'
}
$forbiddenShellSourcePattern = (
    '(?i)(?:' +
    '\b(?:pwsh|powershell)(?:\.exe)?\b|' +
    '\bcmd(?:\.exe)?\s*/[ck]\b|' +
    '\b(?:bash|sh)\b[^\r\n]*\s-c\b|' +
    '\b(?:python(?:3|\.exe)?|node(?:\.exe)?)\b[^\r\n]*\s-(?:c|e)\b|' +
    '\[scriptblock\]|' +
    '\b(?:Invoke-Expression|iex|Invoke-Command|icm|NewScriptBlock)\b|' +
    '[&.]\s*\$env:' +
    ')'
)
$allowedUses = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($value in @(
        'actions/attest',
        'actions/checkout',
        'actions/create-github-app-token',
        'actions/download-artifact',
        'actions/setup-dotnet',
        'actions/upload-artifact',
        'wiresock/WireSockUI/.github/workflows/sdk-integration.yml')) {
    [void]$allowedUses.Add($value)
}

function Get-ProductionWorkflowDigest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkflowText
    )

    $normalized = $WorkflowText -replace '\r\n?', "`n"
    $normalized = $normalized.TrimEnd([char[]]"`r`n") + "`n"

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
        return -join @(
            $sha256.ComputeHash($bytes) |
                ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-OrdinaryFileSystemItem {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileSystemInfo] $Item
    )

    $linkType = $Item.PSObject.Properties['LinkType']
    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($null -ne $linkType -and
         -not [string]::IsNullOrEmpty([string]$linkType.Value))) {
        throw "Workflow policy input '$($Item.FullName)' must not be a link or reparse point."
    }
}

function Get-WorkflowLiteralRunScript {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkflowText,

        [Parameter(Mandatory = $true)]
        [string] $StepName,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $stepMarker = "      - name: $StepName"
    $stepStart = $WorkflowText.IndexOf(
        $stepMarker,
        [StringComparison]::Ordinal)
    if ($stepStart -lt 0) {
        throw "$Description is missing step '$StepName'."
    }
    $stepEnd = $WorkflowText.IndexOf(
        '      - name:',
        $stepStart + $stepMarker.Length,
        [StringComparison]::Ordinal)
    if ($stepEnd -lt 0) {
        $stepEnd = $WorkflowText.Length
    }
    $stepLines = @(
        $WorkflowText.Substring($stepStart, $stepEnd - $stepStart) -split
            '\r?\n')
    $runHeaders = @(
        for ($index = 0; $index -lt $stepLines.Count; $index++) {
            if ($stepLines[$index].Trim() -ceq 'run: |') {
                $index
            }
        }
    )
    if ($runHeaders.Count -ne 1) {
        throw "$Description step '$StepName' must contain exactly one literal PowerShell run block."
    }
    return [string]::Join(
        [Environment]::NewLine,
        @($stepLines[($runHeaders[0] + 1)..($stepLines.Count - 1)]))
}

function Assert-WorkflowCriticalStep {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkflowText,

        [Parameter(Mandatory = $true)]
        [string] $StepName,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $stepPattern =
        "(?m)^      - name: $([regex]::Escape($StepName))\s*$"
    $stepMatches = [regex]::Matches($WorkflowText, $stepPattern)
    if ($stepMatches.Count -ne 1) {
        throw "$Description must contain exactly one step named '$StepName'."
    }
    $stepStart = $stepMatches[0].Index
    $nextStep = [regex]::Match(
        $WorkflowText.Substring(
            $stepStart + $stepMatches[0].Length),
        '(?m)^      - (?:name|uses|run):')
    $stepEnd = if ($nextStep.Success) {
        $stepStart + $stepMatches[0].Length + $nextStep.Index
    }
    else {
        $WorkflowText.Length
    }
    $stepSource = $WorkflowText.Substring(
        $stepStart,
        $stepEnd - $stepStart)
    if ($stepSource -match
            '(?m)^        (?:if|continue-on-error):' -or
        [regex]::Matches(
            $stepSource,
            '(?m)^        shell:\s*pwsh\s*$').Count -ne 1 -or
        [regex]::Matches(
            $stepSource,
            '(?m)^        run:\s*(?:[|>]|[^\s#])').Count -ne 1) {
        throw "$Description step '$StepName' must be an unconditional, failure-propagating, canonical pwsh step."
    }
}

function Assert-PowerShellExecutableLines {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Script,

        [Parameter(Mandatory = $true)]
        [string[]] $RequiredLines,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput(
        $Script,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        throw "$Description is not syntactically valid PowerShell."
    }

    $nonExecutableKinds =
        [Collections.Generic.HashSet[
            Management.Automation.Language.TokenKind]]::new()
    foreach ($kind in @(
            [Management.Automation.Language.TokenKind]::Comment,
            [Management.Automation.Language.TokenKind]::StringLiteral,
            [Management.Automation.Language.TokenKind]::StringExpandable,
            [Management.Automation.Language.TokenKind]::HereStringLiteral,
            [Management.Automation.Language.TokenKind]::HereStringExpandable,
            [Management.Automation.Language.TokenKind]::NewLine,
            [Management.Automation.Language.TokenKind]::EndOfInput)) {
        [void]$nonExecutableKinds.Add($kind)
    }
    $executableTokenStartLines =
        [Collections.Generic.HashSet[int]]::new()
    foreach ($token in @($tokens)) {
        if (-not $nonExecutableKinds.Contains($token.Kind)) {
            [void]$executableTokenStartLines.Add(
                $token.Extent.StartLineNumber)
        }
    }

    $scriptLines = @($Script -split '\r?\n')
    foreach ($requiredLine in $RequiredLines) {
        $foundExecutableLine = $false
        for ($index = 0; $index -lt $scriptLines.Count; $index++) {
            if ($scriptLines[$index].Trim() -ceq $requiredLine -and
                $executableTokenStartLines.Contains($index + 1)) {
                $foundExecutableLine = $true
                break
            }
        }
        if (-not $foundExecutableLine) {
            throw "$Description is missing executable check '$requiredLine'."
        }
    }
}

function Get-WorkflowPowerShellCommands {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkflowText,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $workflowLines = @($WorkflowText -split '\r?\n')
    $commandNames = [Collections.Generic.List[string]]::new()
    for ($lineIndex = 0;
        $lineIndex -lt $workflowLines.Count;
        $lineIndex++) {
        $runLine = $workflowLines[$lineIndex]
        if ($runLine -cnotmatch
            '^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$') {
            continue
        }
        $runKeyColumn =
            $runLine.IndexOf('run:', [StringComparison]::Ordinal)
        $runValue = [string]$Matches.value
        if ($runValue -cmatch '^(?<style>[|>])\s*(?:#.*)?$') {
            $style = [string]$Matches.style
            $contentLines = [Collections.Generic.List[string]]::new()
            for ($contentIndex = $lineIndex + 1;
                $contentIndex -lt $workflowLines.Count;
                $contentIndex++) {
                $contentLine = $workflowLines[$contentIndex]
                if ([string]::IsNullOrWhiteSpace($contentLine)) {
                    $contentLines.Add('')
                    continue
                }
                $contentIndent =
                    $contentLine.Length - $contentLine.TrimStart().Length
                if ($contentIndent -le $runKeyColumn) {
                    break
                }
                $removeCount = [Math]::Min(
                    $runKeyColumn + 2,
                    $contentLine.Length)
                $contentLines.Add($contentLine.Substring($removeCount))
            }
            $script = if ($style -ceq '|') {
                [string]::Join(
                    [Environment]::NewLine,
                    @($contentLines))
            }
            else {
                [string]::Join(' ', @($contentLines))
            }
        }
        else {
            $script = $runValue
        }

        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput(
            $script,
            [ref]$tokens,
            [ref]$parseErrors)
        if (@($parseErrors).Count -ne 0) {
            throw "$Description contains a run scalar that is not syntactically valid PowerShell."
        }
        foreach ($commandAst in @(
                $ast.FindAll(
                    {
                        param($node)
                        $node -is [
                            Management.Automation.Language.CommandAst]
                    },
                    $true))) {
            $commandName = $commandAst.GetCommandName()
            if (-not [string]::IsNullOrEmpty($commandName)) {
                $commandNames.Add($commandName)
            }
        }
    }
    return @($commandNames)
}

$workflowRoot = Get-Item -LiteralPath $WorkflowDirectory -Force
Assert-OrdinaryFileSystemItem -Item $workflowRoot
if (-not $workflowRoot.PSIsContainer) {
    throw "Workflow directory '$WorkflowDirectory' is not a directory."
}
$repositoryWorkflowDirectory = [IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent $PSScriptRoot) '.github\workflows')
).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$resolvedWorkflowDirectory = [IO.Path]::GetFullPath(
    $workflowRoot.FullName
).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$enforceProductionContracts =
    $RequireProductionContracts.IsPresent -or
    $resolvedWorkflowDirectory.Equals(
        $repositoryWorkflowDirectory,
        [StringComparison]::OrdinalIgnoreCase)

$workflowFiles = @(
    Get-ChildItem -LiteralPath $workflowRoot.FullName -Force -File |
        Where-Object { $_.Extension -in @('.yml', '.yaml') } |
        Sort-Object Name
)
if ($workflowFiles.Count -lt 1 -or
    $workflowFiles.Count -gt $maximumWorkflowCount) {
    throw "Expected between one and $maximumWorkflowCount workflow files; found $($workflowFiles.Count)."
}

[Int64]$aggregateBytes = 0
foreach ($workflowFile in $workflowFiles) {
    Assert-OrdinaryFileSystemItem -Item $workflowFile
    if ([Int64]$workflowFile.Length -le 0 -or
        [Int64]$workflowFile.Length -gt $maximumWorkflowBytes -or
        [Int64]$workflowFile.Length -gt ($maximumAggregateBytes - $aggregateBytes)) {
        throw "Workflow '$($workflowFile.Name)' is empty or exceeds the bounded policy input size."
    }
    $aggregateBytes += [Int64]$workflowFile.Length

    $text = Get-Content -LiteralPath $workflowFile.FullName -Raw -Encoding UTF8
    $lines = @($text -split '\r?\n')
    # This validator deliberately accepts only a canonical YAML-key subset.
    # GitHub's YAML parser decodes quoted escapes before interpreting keys, so
    # raw-text checks for `uses`, `permissions`, or credential settings can
    # otherwise be bypassed with a key such as "u\u0073es". Anchors, aliases,
    # tags, explicit keys, and merge keys can hide the same security-sensitive
    # mappings. They are unnecessary in these workflows, so reject them rather
    # than trying to duplicate GitHub's evolving YAML parser.
    $quotedMappingKeyPattern = (
        '(?m)(?:^\s*(?:-\s+)?|[\{\[,]\s*)' +
        '(?:!![^\s]+\s+)?' +
        '(?:"(?:\\.|[^"\r\n])*"|''(?:''''|[^''\r\n])*'')\s*:'
    )
    $nonCanonicalNodePattern = (
        '(?m)(?:^\s*(?:-\s+)?|[\{\[,]\s*)' +
        '(?:' +
        '\?\s*|' +
        '<<\s*:|' +
        '(?:[A-Za-z0-9_.-]+\s*:\s*)?!(?:[^\s,:{}\[\]]+)?(?=\s|$)|' +
        '(?:[A-Za-z0-9_.-]+\s*:\s*)?[&*][A-Za-z0-9_.-]+' +
        ')'
    )
    if ($text -match $quotedMappingKeyPattern -or
        $text -match $nonCanonicalNodePattern) {
        throw "Workflow '$($workflowFile.Name)' contains a quoted/escaped key, YAML tag, anchor, alias, explicit key, or merge key outside the canonical policy subset."
    }

    $runContentLineIndexes = [Collections.Generic.HashSet[int]]::new()
    for ($runLineIndex = 0; $runLineIndex -lt $lines.Count; $runLineIndex++) {
        $runLine = $lines[$runLineIndex]
        $containsRunKey =
            $runLine -match '(?i)(?:^|[\s\{\[,])(?:\?\s*)?["'']?run["'']?\s*(?::|$)'
        if (-not $containsRunKey) {
            continue
        }
        if ($runLine -cnotmatch
            '^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$') {
            throw "Workflow '$($workflowFile.Name)' contains a quoted, flow-style, or otherwise noncanonical run key on line $($runLineIndex + 1)."
        }

        $runKeyColumn = $runLine.IndexOf('run:', [StringComparison]::Ordinal)
        $runValue = [string]$Matches.value
        if ([string]::IsNullOrWhiteSpace($runValue)) {
            throw "Workflow '$($workflowFile.Name)' must place each run value on the same line as its canonical run key."
        }
        if ($runValue -match '^\s*["'']' -or
            $runValue -match '^\s*(?:!|[&*][A-Za-z0-9_.-]+)' -or
            $runValue -match '\$\{\{' -or
            $runValue -match $forbiddenShellSourcePattern) {
            throw "Workflow '$($workflowFile.Name)' contains a quoted/tagged/aliased run value, direct expression interpolation, or dynamic code-evaluation primitive on line $($runLineIndex + 1)."
        }

        if ($runValue -match '^[|>]' -and
            $runValue -cnotmatch '^[|>]\s*(?:#.*)?$') {
            throw "Workflow '$($workflowFile.Name)' contains a noncanonical run block-scalar header on line $($runLineIndex + 1)."
        }
        if ($runValue -cnotmatch '^[|>]\s*(?:#.*)?$') {
            for ($continuationLineIndex = $runLineIndex + 1;
                $continuationLineIndex -lt $lines.Count;
                $continuationLineIndex++) {
                $continuationLine = $lines[$continuationLineIndex]
                if ([string]::IsNullOrWhiteSpace($continuationLine) -or
                    $continuationLine.TrimStart().StartsWith('#')) {
                    continue
                }
                $continuationIndent =
                    $continuationLine.Length -
                    $continuationLine.TrimStart().Length
                if ($continuationIndent -le $runKeyColumn) {
                    break
                }
                throw "Workflow '$($workflowFile.Name)' contains a noncanonical multiline plain run scalar on line $($continuationLineIndex + 1)."
            }
            continue
        }
        for ($scriptLineIndex = $runLineIndex + 1;
            $scriptLineIndex -lt $lines.Count;
            $scriptLineIndex++) {
            $scriptLine = $lines[$scriptLineIndex]
            if ([string]::IsNullOrWhiteSpace($scriptLine)) {
                [void]$runContentLineIndexes.Add($scriptLineIndex)
                continue
            }
            $scriptIndent =
                $scriptLine.Length - $scriptLine.TrimStart().Length
            if ($scriptIndent -le $runKeyColumn) {
                break
            }
            [void]$runContentLineIndexes.Add($scriptLineIndex)
            if ($scriptLine -match '\$\{\{' -or
                $scriptLine -match $forbiddenShellSourcePattern) {
                throw "Workflow '$($workflowFile.Name)' interpolates an expression or invokes a dynamic code-evaluation primitive in shell source on line $($scriptLineIndex + 1)."
            }
        }
    }

    $allowedNonRunScalarContentLineIndexes =
        [Collections.Generic.HashSet[int]]::new()
    for ($scalarLineIndex = 0;
        $scalarLineIndex -lt $lines.Count;
        $scalarLineIndex++) {
        if ($runContentLineIndexes.Contains($scalarLineIndex) -or
            $lines[$scalarLineIndex] -cnotmatch
                '^\s*subject-path:\s*\|\s*$') {
            continue
        }
        $subjectPathColumn = $lines[$scalarLineIndex].IndexOf(
            'subject-path:',
            [StringComparison]::Ordinal)
        $subjectPathEntryCount = 0
        for ($subjectLineIndex = $scalarLineIndex + 1;
            $subjectLineIndex -lt $lines.Count;
            $subjectLineIndex++) {
            $subjectLine = $lines[$subjectLineIndex]
            if ([string]::IsNullOrWhiteSpace($subjectLine)) {
                [void]$allowedNonRunScalarContentLineIndexes.Add(
                    $subjectLineIndex)
                continue
            }
            $subjectIndent =
                $subjectLine.Length - $subjectLine.TrimStart().Length
            if ($subjectIndent -le $subjectPathColumn) {
                break
            }
            [void]$allowedNonRunScalarContentLineIndexes.Add(
                $subjectLineIndex)
            $subjectPathEntryCount++
        }
        if ($subjectPathEntryCount -lt 1) {
            throw "Workflow '$($workflowFile.Name)' contains an empty attestation subject-path block."
        }
    }

    $onLineIndexes = @(
        for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
            if ($lines[$lineIndex] -ceq 'on:') {
                $lineIndex
            }
        }
    )
    if ($text.IndexOf([char]0) -ge 0 -or
        $onLineIndexes.Count -ne 1 -or
        $text -notmatch '(?m)^permissions:\s*$' -or
        $text -match '(?im)^\s*["'']?permissions["'']?\s*:\s*["'']?write-all["'']?\s*(?:#.*)?$' -or
        $text -match '(?im)^\s*(?:\?\s*)?["'']?(pull_request_target|workflow_run)["'']?\s*(?::|$)' -or
        $text -match '(?im)^\s*["'']?on["'']?\s*:\s*[\[\{]' -or
        $text -match '(?im)\bpersist-credentials:\s*true\b' -or
        $text -match '(?i)\b(MY_GITHUB_PAT|AZURE_CREDENTIALS|PFX_PASSWORD)\b' -or
        $text -match '(?im)^\s*(Invoke-Expression|iex)\b') {
        throw "Workflow '$($workflowFile.Name)' contains a forbidden trigger, permission, credential mode, secret, or dynamic execution primitive."
    }

    $triggerNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    for ($triggerLineIndex = $onLineIndexes[0] + 1;
        $triggerLineIndex -lt $lines.Count;
        $triggerLineIndex++) {
        $triggerLine = $lines[$triggerLineIndex]
        if ([string]::IsNullOrWhiteSpace($triggerLine) -or
            $triggerLine.TrimStart().StartsWith('#')) {
            continue
        }
        $triggerIndent =
            $triggerLine.Length - $triggerLine.TrimStart().Length
        if ($triggerIndent -eq 0) {
            break
        }
        if ($triggerIndent -lt 2) {
            throw "Workflow '$($workflowFile.Name)' has noncanonical trigger indentation."
        }
        if ($triggerIndent -ne 2) {
            continue
        }
        if ($triggerLine -cnotmatch
            '^  (?<event>push|pull_request|schedule|workflow_call|workflow_dispatch):\s*$' -or
            -not $triggerNames.Add([string]$Matches.event)) {
            throw "Workflow '$($workflowFile.Name)' contains a noncanonical, duplicate, or forbidden event trigger on line $($triggerLineIndex + 1)."
        }
    }
    if ($triggerNames.Count -lt 1) {
        throw "Workflow '$($workflowFile.Name)' must declare at least one canonical event trigger."
    }

    $canonicalUses = [Collections.Generic.List[object]]::new()
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $line = $lines[$lineIndex]
        if ($line -match '(?i)\bsecrets\b' -and
            $line -cnotmatch (
                '^\s*[A-Za-z0-9_.-]+\s*:\s*\$\{\{\s*secrets\.' +
                '(?:RELEASE_POLICY_READER_PRIVATE_KEY|' +
                'WIRESOCK_SDK_READER_PRIVATE_KEY|' +
                'WIRESOCK_SDK_RUNNER_POLICY_PRIVATE_KEY)' +
                '\s*\}\}\s*(?:#.*)?$')) {
            throw "Workflow '$($workflowFile.Name)' contains a computed, embedded, or unaudited secret reference on line $($lineIndex + 1)."
        }
        if ($runContentLineIndexes.Contains($lineIndex) -or
            $allowedNonRunScalarContentLineIndexes.Contains($lineIndex)) {
            continue
        }
        $trimmedLine = $line.TrimStart()
        if (-not [string]::IsNullOrWhiteSpace($line) -and
            -not $trimmedLine.StartsWith('#') -and
            $trimmedLine -cnotmatch
                '^(?:-\s+)?[A-Za-z0-9_.-]+\s*:' -and
            $trimmedLine -cnotmatch '^-\s+\S') {
            throw "Workflow '$($workflowFile.Name)' contains a deferred, multiline, or otherwise noncanonical YAML scalar on line $($lineIndex + 1)."
        }
        $containsRunsOnKey =
            $line -match '(?i)(?:^|[\s\{\[,])(?:\?\s*)?["'']?runs-on["'']?\s*(?::|$)'
        if ($containsRunsOnKey -and
            $trimmedLine -cne 'runs-on: windows-latest' -and
            $trimmedLine -cne
                'runs-on: ${{ matrix.platform == ''ARM64'' && ''windows-11-arm'' || ''windows-latest'' }}' -and
            -not (
                $workflowFile.Name -ceq 'sdk-integration.yml' -and
                $trimmedLine -ceq 'runs-on:')) {
            throw "Workflow '$($workflowFile.Name)' contains a dynamic, self-hosted, or otherwise unaudited runner selector on line $($lineIndex + 1)."
        }
        if ($line -match
            '(?i)(?:^|[\s\{\[,])(?:\?\s*)?["'']?(?:container|services)["'']?\s*(?::|$)') {
            throw "Workflow '$($workflowFile.Name)' contains an unaudited job container or service definition on line $($lineIndex + 1)."
        }
        if ($line -match
            '(?:^\s*(?:-\s+)?[A-Za-z0-9_.-]+\s*:\s*|^\s*-\s*|[\{\[,]\s*(?:[A-Za-z0-9_.-]+\s*:\s*)?)"') {
            throw "Workflow '$($workflowFile.Name)' contains a double-quoted YAML scalar outside the canonical policy subset on line $($lineIndex + 1)."
        }
        $isStandaloneBlockScalar =
            $line -match '^\s*(?:-\s+)?[|>]'
        $isNonRunBlockScalar =
            $line -match
                '^\s*(?:-\s+)?(?!run\s*:)[A-Za-z0-9_.-]+\s*:\s*[|>]'
        $isCanonicalAttestationSubjectBlock =
            $line -cmatch '^\s*subject-path:\s*\|\s*$'
        if ($isStandaloneBlockScalar -or
            ($isNonRunBlockScalar -and
             -not $isCanonicalAttestationSubjectBlock)) {
            throw "Workflow '$($workflowFile.Name)' contains a block scalar outside a canonical same-line run value on line $($lineIndex + 1)."
        }
        $containsShellKey =
            $line -match '(?i)(?:^|[\s\{\[,])(?:\?\s*)?["'']?shell["'']?\s*(?::|$)'
        if ($containsShellKey) {
            if ($line -cnotmatch '^\s*shell:\s*pwsh\s*(?:#.*)?$') {
                throw "Workflow '$($workflowFile.Name)' contains a dynamic or noncanonical shell on line $($lineIndex + 1)."
            }
            $shellKeyColumn =
                $line.IndexOf('shell:', [StringComparison]::Ordinal)
            for ($shellContinuationIndex = $lineIndex + 1;
                $shellContinuationIndex -lt $lines.Count;
                $shellContinuationIndex++) {
                $shellContinuation = $lines[$shellContinuationIndex]
                if ([string]::IsNullOrWhiteSpace($shellContinuation) -or
                    $shellContinuation.TrimStart().StartsWith('#')) {
                    continue
                }
                $shellContinuationIndent =
                    $shellContinuation.Length -
                    $shellContinuation.TrimStart().Length
                if ($shellContinuationIndent -le $shellKeyColumn) {
                    break
                }
                throw "Workflow '$($workflowFile.Name)' contains a multiline shell scalar on line $($shellContinuationIndex + 1)."
            }
        }
        if ($line -match
            '(?i)(?:^|[\s\{\[,])(?:\?\s*)?["'']?working-directory["'']?\s*(?::|$)') {
            throw "Workflow '$($workflowFile.Name)' contains a working-directory override outside the canonical policy subset on line $($lineIndex + 1)."
        }
        if ($line -match
                '(?i)(?:^|[\s\{\[,])["'']?persist-credentials["'']?\s*:' -and
            $line -cnotmatch '^\s*persist-credentials:\s*false\s*$') {
            throw "Workflow '$($workflowFile.Name)' contains a noncanonical or unsafe persisted-credential setting on line $($lineIndex + 1)."
        }

        $containsUsesKey =
            $line -match '(?i)(?:^|[\s\{\[,])(?:\?\s*)?["'']?uses["'']?\s*(?::|$)'
        if (-not $containsUsesKey) {
            continue
        }
        if ($line -cnotmatch
            '^(?<indent>\s*)(?:-\s+)?uses:\s*(?<value>[^\s#]+)\s*(?:#.*)?$') {
            throw "Workflow '$($workflowFile.Name)' contains a quoted, flow-style, or otherwise noncanonical uses key on line $($lineIndex + 1)."
        }

        $usesValue = [string]$Matches.value
        if ($usesValue -notmatch '^(?<target>[^@]+)@(?<revision>[0-9a-f]{40})$') {
            throw "Workflow '$($workflowFile.Name)' has mutable or malformed uses reference '$usesValue'."
        }
        if (-not $allowedUses.Contains([string]$Matches.target)) {
            throw "Workflow '$($workflowFile.Name)' uses unaudited dependency '$($Matches.target)'."
        }
        $canonicalUses.Add([pscustomobject]@{
                LineIndex = $lineIndex
                UsesColumn = $line.IndexOf('uses:', [StringComparison]::Ordinal)
                Value = $usesValue
            })
        $totalUsesCount++
    }

    foreach ($canonicalUse in $canonicalUses) {
        if ($canonicalUse.Value -cnotmatch
            '^actions/checkout@[0-9a-f]{40}$') {
            continue
        }

        $checkoutIndent = [int]$canonicalUse.UsesColumn
        $withFound = $false
        $credentialPolicyCount = 0
        $foundCredentialPolicy = $false
        for ($candidateIndex = [int]$canonicalUse.LineIndex + 1;
            $candidateIndex -lt $lines.Count;
            $candidateIndex++) {
            $candidate = $lines[$candidateIndex]
            if ($candidate -match '^\s*-\s+' -and
                $candidate.IndexOf('-') -lt $checkoutIndent) {
                break
            }

            $leadingWhitespace =
                $candidate.Length - $candidate.TrimStart().Length
            if ($candidate -cmatch '^\s*with:\s*$') {
                if ($leadingWhitespace -ne $checkoutIndent -or $withFound) {
                    throw "Checkout in '$($workflowFile.Name)' has a noncanonical or duplicate with block."
                }
                $withFound = $true
                continue
            }
            if ($candidate -cmatch '^\s*persist-credentials:\s*false\s*$') {
                if (-not $withFound -or
                    $leadingWhitespace -ne ($checkoutIndent + 2)) {
                    throw "Checkout in '$($workflowFile.Name)' has a misplaced persisted-credential policy."
                }
                $credentialPolicyCount++
                $foundCredentialPolicy = $true
            }
        }
        if (-not $foundCredentialPolicy -or $credentialPolicyCount -ne 1) {
            throw "Checkout in '$($workflowFile.Name)' must explicitly disable persisted credentials."
        }
    }
}

if ($enforceProductionContracts) {
    $expectedProductionWorkflowNames =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
    foreach ($workflowName in $productionWorkflowDigests.Keys) {
        [void]$expectedProductionWorkflowNames.Add($workflowName)
    }
    if ($workflowFiles.Count -ne $expectedProductionWorkflowNames.Count) {
        throw (
            'The production workflow set must contain exactly ' +
            "$($expectedProductionWorkflowNames.Count) audited files.")
    }
    foreach ($workflowFile in $workflowFiles) {
        if (-not $expectedProductionWorkflowNames.Contains(
                $workflowFile.Name)) {
            throw (
                "Unexpected production workflow '$($workflowFile.Name)'; " +
                'every production workflow requires an audited contract.')
        }
    }

    foreach ($workflowName in @(
            $productionWorkflowDigests.Keys | Sort-Object)) {
        $workflowPath = Join-Path $workflowRoot.FullName $workflowName
        if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
            throw "Required production workflow '$workflowName' is missing."
        }
        $workflowText = Get-Content `
            -LiteralPath $workflowPath `
            -Raw `
            -Encoding UTF8
        $actualDigest = Get-ProductionWorkflowDigest `
            -WorkflowText $workflowText
        $expectedDigest = $productionWorkflowDigests[$workflowName]
        if ($actualDigest -cne $expectedDigest) {
            throw (
                "Production workflow '$workflowName' does not match its " +
                "audited control contract (expected $expectedDigest, got " +
                "$actualDigest). Update the digest only after an explicit " +
                'trust-boundary review.')
        }
    }

    $productionText = [string]::Join(
        "`n",
        @($workflowFiles | ForEach-Object {
                Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
            }))
    $callerPins = @(
        [regex]::Matches(
            $productionText,
            'wiresock/WireSockUI/\.github/workflows/' +
                'sdk-integration\.yml@' +
                '(?<sha>[0-9a-f]{40})') |
            ForEach-Object { $_.Groups['sha'].Value })
    $sdkInputPins = @(
        [regex]::Matches(
            $productionText,
            '(?m)^\s+sdk_workflow_sha:\s*(?<sha>[0-9a-f]{40})\s*$') |
            ForEach-Object { $_.Groups['sha'].Value })
    if ($callerPins.Count -ne 3 -or $sdkInputPins.Count -ne 3) {
        throw (
            'Production workflows must contain exactly three privileged ' +
            'reusable-workflow callers and three SDK workflow SHA inputs.')
    }
    $allProductionPins = @($callerPins) + @($sdkInputPins)
    if (@($allProductionPins | Sort-Object -Unique).Count -ne 1) {
        throw (
            'All privileged reusable-workflow callers and SDK workflow SHA ' +
            'inputs must use the same immutable implementation revision.')
    }
}

$hostedSdkExperimentPath =
    Join-Path $workflowRoot.FullName 'hosted-sdk-experiment.yml'
if (Test-Path -LiteralPath $hostedSdkExperimentPath -PathType Leaf) {
    $hostedSdkExperimentWorkflow = Get-Content `
        -LiteralPath $hostedSdkExperimentPath `
        -Raw `
        -Encoding UTF8
    Assert-WorkflowCriticalStep `
        -WorkflowText $hostedSdkExperimentWorkflow `
        -StepName 'Authorize protected default-branch revision' `
        -Description 'The hosted SDK experiment workflow'
    $hostedSdkAuthorizationScript = Get-WorkflowLiteralRunScript `
        -WorkflowText $hostedSdkExperimentWorkflow `
        -StepName 'Authorize protected default-branch revision' `
        -Description 'The hosted SDK experiment workflow'
    Assert-PowerShellExecutableLines `
        -Script $hostedSdkAuthorizationScript `
        -Description 'The hosted SDK experiment authorization step' `
        -RequiredLines @(
            'if ($env:GITHUB_REPOSITORY -cne ''wiresock/WireSockUI'') {',
            'if ($env:EVENT_NAME -notin @(''push'', ''schedule'', ''workflow_dispatch'')) {',
            'if ($env:WORKFLOW_REF -cne $expectedRef) {',
            '$trustedSha -cne $defaultBranchSha) {')
    $hostedSdkExecutableLines =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
    foreach ($hostedSdkLine in
        ($hostedSdkExperimentWorkflow -split '\r?\n')) {
        [void]$hostedSdkExecutableLines.Add($hostedSdkLine.Trim())
    }
    foreach ($requiredHostedSdkLine in @(
            "- cron: '17 5 * * 1'",
            'cancel-in-progress: true',
            "runs-on: `${{ matrix.platform == 'ARM64' && 'windows-11-arm' || 'windows-latest' }}",
            "platform: ['x64', 'ARM64']",
            'ref: ${{ needs.authorize.outputs.trusted_sha }}',
            'TEST_PLATFORM: ${{ matrix.platform }}',
            'run: ./scripts/Invoke-HostedSdkExperiment.ps1 -Platform $env:TEST_PLATFORM')) {
        if (-not $hostedSdkExecutableLines.Contains(
                $requiredHostedSdkLine)) {
            throw "The hosted SDK experiment is missing matrix trust boundary '$requiredHostedSdkLine'."
        }
    }
    if ([regex]::Matches(
            $hostedSdkExperimentWorkflow,
            '(?m)^\s+run:\s+\./scripts/Invoke-HostedSdkExperiment\.ps1\s').Count -ne 1) {
        throw 'The hosted SDK matrix must invoke its architecture-validated experiment script exactly once.'
    }
}

$sdkIntegrationPath =
    Join-Path $workflowRoot.FullName 'sdk-integration.yml'
if (Test-Path -LiteralPath $sdkIntegrationPath -PathType Leaf) {
    $sdkIntegrationWorkflow = Get-Content `
        -LiteralPath $sdkIntegrationPath `
        -Raw `
        -Encoding UTF8
    foreach ($criticalSdkStep in @(
            'Authorize trusted candidate revision',
            'Verify trusted runner-policy tooling checkout',
            'Validate pinned reusable-workflow SHA',
            'Verify exact SDK runner-group policy')) {
        Assert-WorkflowCriticalStep `
            -WorkflowText $sdkIntegrationWorkflow `
            -StepName $criticalSdkStep `
            -Description 'The SDK integration workflow'
    }
    $sdkExecutableLines =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
    foreach ($sdkLine in ($sdkIntegrationWorkflow -split '\r?\n')) {
        [void]$sdkExecutableLines.Add($sdkLine.Trim())
    }
    foreach ($requiredSdkLine in @(
            'repository: ${{ job.workflow_repository }}',
            'ref: ${{ job.workflow_sha }}',
            'path: .trusted-sdk-tooling',
            'group: wiresock-sdk',
            'labels:',
            '- self-hosted',
            '- windows',
            '- ${{ matrix.hardware_label }}',
            '- wiresock-sdk',
            '- ${{ matrix.routing_label }}')) {
        if (-not $sdkExecutableLines.Contains($requiredSdkLine)) {
            throw "The SDK integration workflow is missing trusted-tooling boundary '$requiredSdkLine'."
        }
    }
    $sdkAuthorizationScript = Get-WorkflowLiteralRunScript `
        -WorkflowText $sdkIntegrationWorkflow `
        -StepName 'Authorize trusted candidate revision' `
        -Description 'The SDK integration workflow'
    Assert-PowerShellExecutableLines `
        -Script $sdkAuthorizationScript `
        -Description 'The SDK integration authorization step' `
        -RequiredLines @(
            '$env:LOADED_WORKFLOW_REPOSITORY -cne ''wiresock/WireSockUI'' -or',
            '$env:LOADED_WORKFLOW_FILE_PATH -cne ''.github/workflows/sdk-integration.yml'') {',
            '$env:REQUESTED_WORKFLOW_SHA -cne $env:LOADED_WORKFLOW_SHA) {')
    $sdkToolingVerificationScript = Get-WorkflowLiteralRunScript `
        -WorkflowText $sdkIntegrationWorkflow `
        -StepName 'Verify trusted runner-policy tooling checkout' `
        -Description 'The SDK integration workflow'
    Assert-PowerShellExecutableLines `
        -Script $sdkToolingVerificationScript `
        -Description 'The trusted SDK tooling verification step' `
        -RequiredLines @(
            "git -C `$tooling.FullName rev-parse 'HEAD^{commit}'",
            '$toolingSha -cne $env:LOADED_WORKFLOW_SHA) {')
    $sdkWorkflowShaScript = Get-WorkflowLiteralRunScript `
        -WorkflowText $sdkIntegrationWorkflow `
        -StepName 'Validate pinned reusable-workflow SHA' `
        -Description 'The SDK integration workflow'
    Assert-PowerShellExecutableLines `
        -Script $sdkWorkflowShaScript `
        -Description 'The SDK reusable-workflow SHA validation step' `
        -RequiredLines @(
            '$env:SDK_WORKFLOW_SHA -cne $env:LOADED_WORKFLOW_SHA) {')
    if ([regex]::Matches(
            $sdkIntegrationWorkflow,
            '(?m)^\s+runs-on:\s*$').Count -ne 1 -or
        [regex]::Matches(
            $sdkIntegrationWorkflow,
            '(?m)^\s+group:\s*wiresock-sdk\s*$').Count -ne 1) {
        throw 'The SDK integration workflow must contain exactly one canonical protected runner-group selector.'
    }
    $sdkCommands = @(
        Get-WorkflowPowerShellCommands `
            -WorkflowText $sdkIntegrationWorkflow `
            -Description 'The SDK integration workflow')
    $runnerPolicyCommands = @(
        $sdkCommands |
            Where-Object {
                $_ -match
                    '(?i)(?:^|[\\/])Test-SdkRunnerPolicy\.ps1$'
            })
    if ($runnerPolicyCommands.Count -ne 1 -or
        [string]$runnerPolicyCommands[0] -cne
            './.trusted-sdk-tooling/scripts/Test-SdkRunnerPolicy.ps1') {
        throw 'The SDK runner policy must be validated exactly once by workflow-pinned trusted tooling.'
    }
}

$mainWorkflow = Get-Content `
    -LiteralPath (Join-Path $workflowRoot.FullName 'main.yml') `
    -Raw `
    -Encoding UTF8
if ($enforceProductionContracts) {
    Assert-WorkflowCriticalStep `
        -WorkflowText $mainWorkflow `
        -StepName 'Verify signed release tag and protected-branch ancestry' `
        -Description 'The release workflow'
    $releaseAuthorizationScript = Get-WorkflowLiteralRunScript `
        -WorkflowText $mainWorkflow `
        -StepName 'Verify signed release tag and protected-branch ancestry' `
        -Description 'The release workflow'
    Assert-PowerShellExecutableLines `
        -Script $releaseAuthorizationScript `
        -Description 'The release authorization step' `
        -RequiredLines @(
            'foreach ($workflow in @($env:CI_WORKFLOW, $env:HOSTED_SDK_WORKFLOW)) {',
            '[string]$runs.workflow_runs[0].head_sha -cne $trustedSha -or',
            '[string]$runs.workflow_runs[0].conclusion -cne ''success'') {')
}
if ($mainWorkflow -notmatch "(?m)^\s+- 'release-v\*\.\*\.\*'\s*$" -or
    $mainWorkflow -match "(?m)^\s+- 'v\*") {
    throw 'The release workflow must use only the protected release-vMAJOR.MINOR.PATCH namespace.'
}

Write-Output (
    "Validated $($workflowFiles.Count) workflow files, $totalUsesCount " +
    "uses references, full-SHA pins, checkout isolation, and release trigger policy.")
