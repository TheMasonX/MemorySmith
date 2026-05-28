<#
.SYNOPSIS
Captures a repeatable code-search query latency and top-hit baseline through the running MCP endpoint.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5091',
    [int]$RepeatCount = 3,
    [int]$Limit = 5,
    [string[]]$Targets = @('MemorySmith.App', 'MemorySmith.Core', 'MemorySmith.Storage', 'MemorySmith.Tests', 'Scripts'),
    [switch]$RebuildIfStale,
    [switch]$SkipCertificateCheck,
    [string]$ApiKey,
    [string]$SettingsOverridePath,
    [string]$SummaryPath = 'artifacts/browser-validation/code-search-query-baseline.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$SettingsOverridePath = if ([string]::IsNullOrWhiteSpace($SettingsOverridePath)) {
    Join-Path $repoRoot 'artifacts\MemorySmith.App\appsettings.LocalOverrides.json'
}
else {
    [System.IO.Path]::GetFullPath($SettingsOverridePath)
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
    if ($null -eq $contentProperty -or $contentProperty.Value.Count -eq 0) {
        throw "MCP tool '$ToolName' returned no content payload."
    }

    $text = [string]$contentProperty.Value[0].text
    $isErrorProperty = $result.PSObject.Properties['isError']
    if ($null -ne $isErrorProperty -and [bool]$isErrorProperty.Value) {
        throw "MCP tool '$ToolName' failed: $text"
    }

    return [string]$text
}

function Get-Median {
    param([double[]]$Values)

    if ($Values.Count -eq 0) {
        return 0
    }

    $sorted = $Values | Sort-Object
    $middle = [int]($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) {
        return [double]$sorted[$middle]
    }

    return [Math]::Round((([double]$sorted[$middle - 1]) + ([double]$sorted[$middle])) / 2, 3)
}

$endpoint = ([System.Uri]::new([System.Uri]::new($BaseUrl.TrimEnd('/') + '/'), 'mcp')).AbsoluteUri
$queries = @(
    [ordered]@{ Name = 'semantic provider path'; Query = 'unsupported execution provider onnx session initialization' },
    [ordered]@{ Name = 'query telemetry'; Query = 'code search query timing telemetry slow threshold' },
    [ordered]@{ Name = 'vector prefilter'; Query = 'vector candidate prefilter code search lexical fallback' },
    [ordered]@{ Name = 'benchmark harness'; Query = 'warm code search index summary benchmark script' },
    [ordered]@{ Name = 'page validation'; Query = 'page path literals and page links validation script' },
    [ordered]@{ Name = 'semantic provider tests'; Query = 'semantic embedding path tests unsupported execution provider' }
)

$results = New-Object System.Collections.Generic.List[object]
foreach ($querySpec in $queries) {
    $samples = New-Object System.Collections.Generic.List[double]
    $sampleResults = New-Object System.Collections.Generic.List[object]

    for ($run = 1; $run -le [Math]::Max(1, $RepeatCount); $run++) {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $payloadText = Invoke-McpTool -Endpoint $endpoint -ToolName 'memorysmith_code_search' -Arguments @{
            query = $querySpec.Query
            targets = $Targets
            limit = $Limit
            rebuildIfStale = [bool]$RebuildIfStale
            forceRebuild = $false
        } -SkipTlsValidation $SkipCertificateCheck
        $stopwatch.Stop()

        $payload = ConvertFrom-Json -InputObject $payloadText -AsHashtable
        $topResult = $null
        $payloadResults = @($payload['results'])
        if ($payloadResults.Count -gt 0) {
            $topResult = $payloadResults[0]
        }

        $elapsedMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        $samples.Add($elapsedMs)
        $sampleResults.Add([ordered]@{
            Run = $run
            ElapsedMilliseconds = $elapsedMs
            ResultCount = [int]$payload['resultCount']
            TopDocumentPath = if ($null -ne $topResult) { [string]$topResult['documentPath'] } else { $null }
            TopScore = if ($null -ne $topResult) { [double]$topResult['score'] } else { $null }
            TopMatchReason = if ($null -ne $topResult) { [string]$topResult['matchReason'] } else { $null }
            ProviderMode = [string]$payload['status']['providerMode']
            IndexedChunkCount = [int]$payload['status']['indexedChunkCount']
        }) | Out-Null
    }

    $results.Add([ordered]@{
        Name = $querySpec.Name
        Query = $querySpec.Query
        Targets = $Targets
        RepeatCount = [Math]::Max(1, $RepeatCount)
        AverageMilliseconds = [Math]::Round((($samples | Measure-Object -Average).Average), 3)
        MedianMilliseconds = Get-Median -Values $samples.ToArray()
        MinMilliseconds = [double](($samples | Measure-Object -Minimum).Minimum)
        MaxMilliseconds = [double](($samples | Measure-Object -Maximum).Maximum)
        Samples = $sampleResults
    }) | Out-Null
}

$summary = [ordered]@{
    CapturedAtUtc = [DateTime]::UtcNow.ToString('O')
    BaseUrl = $BaseUrl
    McpEndpoint = $endpoint
    RepeatCount = [Math]::Max(1, $RepeatCount)
    Limit = $Limit
    Targets = $Targets
    RebuildIfStale = [bool]$RebuildIfStale
    Queries = $results
}

$summaryJson = $summary | ConvertTo-Json -Depth 20
$fullSummaryPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SummaryPath))
$directory = Split-Path $fullSummaryPath -Parent
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Set-Content -Path $fullSummaryPath -Value $summaryJson -Encoding UTF8
Write-Host "Summary written to: $fullSummaryPath"
Write-Host $summaryJson