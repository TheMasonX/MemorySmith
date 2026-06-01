<#
.SYNOPSIS
Runs a fixed relevance suite against memorysmith_code_search and outputs pass/fail scoring.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5091',
    [string]$SuitePath = 'Scripts/code-search-relevance-suite.json',
    [int]$RepeatCount = 3,
    [switch]$RebuildIfStale,
    [switch]$ForceRebuild,
    [switch]$SkipCertificateCheck,
    [string]$ApiKey,
    [string]$SettingsOverridePath,
    [string]$SummaryPath = 'artifacts/browser-validation/code-search-relevance-summary.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$resolvedSuitePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SuitePath))
$resolvedSummaryPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SummaryPath))
$SettingsOverridePath = if ([string]::IsNullOrWhiteSpace($SettingsOverridePath)) {
    Join-Path $repoRoot 'artifacts\MemorySmith.App\appsettings.LocalOverrides.json'
}
else {
    [System.IO.Path]::GetFullPath($SettingsOverridePath)
}

if (-not (Test-Path $resolvedSuitePath)) {
    throw "Suite file not found: $resolvedSuitePath"
}

if ([string]::IsNullOrWhiteSpace($ApiKey) -and (Test-Path $SettingsOverridePath)) {
    $settings = Get-Content $SettingsOverridePath -Raw | ConvertFrom-Json
    $ApiKey = [string]$settings.MemorySmith.ApiKey
}

function Invoke-McpRequest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Endpoint,

        [Parameter(Mandatory = $true)]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [hashtable]$Params,

        [Parameter(Mandatory = $true)]
        [bool]$SkipTlsValidation
    )

    $body = @{
        jsonrpc = '2.0'
        id = [Guid]::NewGuid().ToString('N')
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 20

    $arguments = @{
        Uri = $Endpoint
        Method = 'Post'
        ContentType = 'application/json'
        Body = $body
        TimeoutSec = 3600
    }

    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        $arguments['Headers'] = @{ 'X-Api-Key' = $ApiKey }
    }

    if ($SkipTlsValidation) {
        $arguments['SkipCertificateCheck'] = $true
    }

    return Invoke-RestMethod @arguments
}

function Invoke-McpTool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Endpoint,

        [Parameter(Mandatory = $true)]
        [string]$ToolName,

        [Parameter(Mandatory = $true)]
        [hashtable]$Arguments,

        [Parameter(Mandatory = $true)]
        [bool]$SkipTlsValidation
    )

    $response = Invoke-McpRequest -Endpoint $Endpoint -Method 'tools/call' -Params @{ name = $ToolName; arguments = $Arguments } -SkipTlsValidation $SkipTlsValidation
    $errorProperty = $response.PSObject.Properties['error']
    if ($null -ne $errorProperty -and $null -ne $errorProperty.Value) {
        throw "MCP call failed: $($errorProperty.Value.message)"
    }

    $resultProperty = $response.PSObject.Properties['result']
    $result = if ($null -ne $resultProperty) { $resultProperty.Value } else { $null }
    if ($null -eq $result) {
        throw "MCP tool '$ToolName' did not return a result payload."
    }

    $contentProperty = $result.PSObject.Properties['content']
    $contentItems = if ($null -ne $contentProperty) { @($contentProperty.Value) } else { @() }
    if ($contentItems.Count -eq 0) {
        throw "MCP tool '$ToolName' returned no content payload."
    }

    $text = [string]$contentItems[0].text
    $isErrorProperty = $result.PSObject.Properties['isError']
    if ($null -ne $isErrorProperty -and [bool]$isErrorProperty.Value) {
        throw "MCP tool '$ToolName' failed: $text"
    }

    return [string]$text
}

function Test-PathPrefix {
    param(
        [AllowNull()]
        [string]$Path,
        [string[]]$Prefixes
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $null -eq $Prefixes -or $Prefixes.Count -eq 0) {
        return $false
    }

    foreach ($prefix in $Prefixes) {
        if (-not [string]::IsNullOrWhiteSpace($prefix) -and $Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-Median {
    param([double[]]$Values)

    $normalizedValues = @($Values)
    if ($normalizedValues.Count -eq 0) {
        return 0
    }

    $sorted = @($normalizedValues | Sort-Object)
    $middle = [int]($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) {
        return [double]$sorted[$middle]
    }

    return [Math]::Round((([double]$sorted[$middle - 1]) + ([double]$sorted[$middle])) / 2, 3)
}

$suite = Get-Content $resolvedSuitePath -Raw | ConvertFrom-Json -AsHashtable
$defaultTargets = @($suite['targets'])
$cases = @($suite['cases'])
if (@($cases).Count -eq 0) {
    throw "No relevance cases found in suite: $resolvedSuitePath"
}

$endpoint = ([System.Uri]::new([System.Uri]::new($BaseUrl.TrimEnd('/') + '/'), 'mcp')).AbsoluteUri
$normalizedRepeatCount = [Math]::Max(1, $RepeatCount)
$shouldForceRebuild = [bool]$ForceRebuild
$caseSummaries = New-Object System.Collections.Generic.List[object]

foreach ($case in $cases) {
    $caseName = [string]$case['name']
    $query = [string]$case['query']
    $limit = if ($case.ContainsKey('limit') -and $null -ne $case['limit']) { [int]$case['limit'] } else { 5 }
    $targets = if ($case.ContainsKey('targets') -and @($case['targets']).Count -gt 0) { @($case['targets']) } else { $defaultTargets }

    $expectedTopPathPrefixes = if ($case.ContainsKey('expectedTopPathPrefixes')) { @($case['expectedTopPathPrefixes']) } else { @() }
    $forbiddenTopPathPrefixes = if ($case.ContainsKey('forbiddenTopPathPrefixes')) { @($case['forbiddenTopPathPrefixes']) } else { @() }
    $requiredInTopN = if ($case.ContainsKey('requiredInTopN')) { @($case['requiredInTopN']) } else { @() }

    $runDetails = New-Object System.Collections.Generic.List[object]
    $elapsedSamples = New-Object System.Collections.Generic.List[double]

    for ($run = 1; $run -le $normalizedRepeatCount; $run++) {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $payloadText = Invoke-McpTool -Endpoint $endpoint -ToolName 'memorysmith_code_search' -Arguments @{
            query = $query
            targets = $targets
            limit = $limit
            rebuildIfStale = [bool]$RebuildIfStale
            forceRebuild = [bool]$shouldForceRebuild
        } -SkipTlsValidation $SkipCertificateCheck
        $shouldForceRebuild = $false
        $stopwatch.Stop()

        $payload = ConvertFrom-Json -InputObject $payloadText -AsHashtable
        $results = @($payload['results'])
        $topResult = if ($results.Count -gt 0) { $results[0] } else { $null }
        $topPath = if ($null -ne $topResult) { [string]$topResult['documentPath'] } else { $null }

        $hasExpectedTopConstraint = @($expectedTopPathPrefixes).Count -gt 0
        $hasForbiddenTopConstraint = @($forbiddenTopPathPrefixes).Count -gt 0
        $hasRequiredInTopNConstraint = @($requiredInTopN).Count -gt 0

        $topExpectedMatch = if ($hasExpectedTopConstraint) { Test-PathPrefix -Path $topPath -Prefixes $expectedTopPathPrefixes } else { $true }
        $topForbiddenMatch = if ($hasForbiddenTopConstraint) { Test-PathPrefix -Path $topPath -Prefixes $forbiddenTopPathPrefixes } else { $false }

        $requiredMatches = @()
        foreach ($requiredPrefix in $requiredInTopN) {
            $requiredMatches += ($results | Where-Object { [string]$_.documentPath -like "$requiredPrefix*" } | Select-Object -First 1)
        }
        $requiredSatisfied = if ($hasRequiredInTopNConstraint) {
            (@($requiredMatches | Where-Object { $null -ne $_ })).Count -eq @($requiredInTopN).Count
        }
        else {
            $true
        }

        $elapsedMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        $elapsedSamples.Add($elapsedMs)

        $passed = $topExpectedMatch -and (-not $topForbiddenMatch) -and $requiredSatisfied
        $runDetails.Add([ordered]@{
            Run = $run
            Passed = [bool]$passed
            ElapsedMilliseconds = $elapsedMs
            ResultCount = [int]$payload['resultCount']
            TopPath = $topPath
            TopScore = if ($null -ne $topResult) { [double]$topResult['score'] } else { $null }
            TopMatchReason = if ($null -ne $topResult) { [string]$topResult['matchReason'] } else { $null }
            TopExpectedMatch = [bool]$topExpectedMatch
            TopForbiddenMatch = [bool]$topForbiddenMatch
            RequiredInTopNSatisfied = [bool]$requiredSatisfied
            ProviderMode = [string]$payload['status']['providerMode']
            IndexedChunkCount = [int]$payload['status']['indexedChunkCount']
            TopPaths = @($results | Select-Object -ExpandProperty documentPath)
        }) | Out-Null
    }

    $passCount = (@($runDetails | Where-Object { $_.Passed })).Count
    $minPassCount = [Math]::Ceiling($normalizedRepeatCount / 2.0)

    $caseSummaries.Add([ordered]@{
        Name = $caseName
        Query = $query
        Targets = $targets
        Limit = $limit
        RepeatCount = $normalizedRepeatCount
        MinPassCount = $minPassCount
        PassCount = $passCount
        Passed = [bool]($passCount -ge $minPassCount)
        AverageMilliseconds = [Math]::Round((($elapsedSamples | Measure-Object -Average).Average), 3)
        MedianMilliseconds = Get-Median -Values $elapsedSamples.ToArray()
        MinMilliseconds = [double](($elapsedSamples | Measure-Object -Minimum).Minimum)
        MaxMilliseconds = [double](($elapsedSamples | Measure-Object -Maximum).Maximum)
        ExpectedTopPathPrefixes = $expectedTopPathPrefixes
        ForbiddenTopPathPrefixes = $forbiddenTopPathPrefixes
        RequiredInTopN = $requiredInTopN
        Runs = $runDetails
    }) | Out-Null
}

$totalCases = $caseSummaries.Count
$passedCases = (@($caseSummaries | Where-Object { $_.Passed })).Count
$failedCases = $totalCases - $passedCases

$summary = [ordered]@{
    CapturedAtUtc = [DateTime]::UtcNow.ToString('O')
    BaseUrl = $BaseUrl
    McpEndpoint = $endpoint
    SuitePath = $resolvedSuitePath
    RepeatCount = $normalizedRepeatCount
    RebuildIfStale = [bool]$RebuildIfStale
    ForceRebuild = [bool]$ForceRebuild
    TotalCases = $totalCases
    PassedCases = $passedCases
    FailedCases = $failedCases
    PassRate = if ($totalCases -gt 0) { [Math]::Round(($passedCases / [double]$totalCases) * 100, 2) } else { 0 }
    Cases = $caseSummaries
}

$summaryJson = $summary | ConvertTo-Json -Depth 30
$summaryDirectory = Split-Path $resolvedSummaryPath -Parent
if (-not (Test-Path $summaryDirectory)) {
    New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
}

Set-Content -Path $resolvedSummaryPath -Value $summaryJson -Encoding UTF8
Write-Host "Summary written to: $resolvedSummaryPath"
Write-Host $summaryJson
