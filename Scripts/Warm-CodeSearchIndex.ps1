<#
.SYNOPSIS
Builds or refreshes the MemorySmith repo code-search SQLite vector index through the running MCP endpoint and reports timing.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://localhost:7090',
    [string]$Query = 'MemorySmith code search warmup',
    [string[]]$Targets = @('MemorySmith.App', 'MemorySmith.Core', 'MemorySmith.Storage', 'MemorySmith.Tests', 'MemorySmith.Benchmarks'),
    [int]$Limit = 5,
    [switch]$ForceRebuild,
    [switch]$SkipCertificateCheck,
    [string]$ApiKey,
    [string]$SettingsOverridePath,
    [string]$SummaryPath
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

$endpoint = ([System.Uri]::new([System.Uri]::new($BaseUrl.TrimEnd('/') + '/'), 'mcp')).AbsoluteUri

Write-Host "==> Warm code-search index via $endpoint"
$beforeText = Invoke-McpTool -Endpoint $endpoint -ToolName 'memorysmith_code_search_status' -Arguments @{} -SkipTlsValidation $SkipCertificateCheck
$beforeStatus = ConvertFrom-Json -InputObject $beforeText -AsHashtable

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$null = Invoke-McpTool -Endpoint $endpoint -ToolName 'memorysmith_code_search' -Arguments @{
    query = $Query
    targets = $Targets
    limit = $Limit
    rebuildIfStale = $true
    forceRebuild = [bool]$ForceRebuild
} -SkipTlsValidation $SkipCertificateCheck
$stopwatch.Stop()

$afterText = Invoke-McpTool -Endpoint $endpoint -ToolName 'memorysmith_code_search_status' -Arguments @{} -SkipTlsValidation $SkipCertificateCheck
$afterStatus = ConvertFrom-Json -InputObject $afterText -AsHashtable

$indexPath = [string]$afterStatus['indexPath']
$indexFile = if (-not [string]::IsNullOrWhiteSpace($indexPath) -and (Test-Path $indexPath)) { Get-Item $indexPath } else { $null }
$build = $afterStatus['build']
$timings = if ($null -ne $build) { $build['timings'] } else { $null }
$buildDuration = $null
if ($null -ne $build['startedAtUtc'] -and $null -ne $build['completedAtUtc']) {
    $buildDuration = [DateTimeOffset]::Parse($build['completedAtUtc']) - [DateTimeOffset]::Parse($build['startedAtUtc'])
}

$buildDurationMs = if ($null -ne $buildDuration) { [int][Math]::Round($buildDuration.TotalMilliseconds) } else { $null }
$filesPerSecond = if ($null -ne $buildDuration -and $buildDuration.TotalSeconds -gt 0) { [Math]::Round(([double]$build['processedFileCount']) / $buildDuration.TotalSeconds, 2) } else { $null }
$chunksPerSecond = if ($null -ne $buildDuration -and $buildDuration.TotalSeconds -gt 0) { [Math]::Round(([double]$afterStatus['indexedChunkCount']) / $buildDuration.TotalSeconds, 2) } else { $null }
$averageEmbeddingMilliseconds = if ($null -ne $timings -and $null -ne $timings['averageEmbeddingMilliseconds']) { [double]$timings['averageEmbeddingMilliseconds'] } else { $null }

$summary = [ordered]@{
    BaseUrl = $BaseUrl
    McpEndpoint = $endpoint
    Query = $Query
    Targets = $Targets
    ForceRebuild = [bool]$ForceRebuild
    ElapsedMilliseconds = [int][Math]::Round($stopwatch.Elapsed.TotalMilliseconds)
    BuildDurationMilliseconds = $buildDurationMs
    IndexedFileCount = [int]$afterStatus['indexedFileCount']
    IndexedChunkCount = [int]$afterStatus['indexedChunkCount']
    ProviderMode = [string]$afterStatus['providerMode']
    ProviderStatus = [string]$afterStatus['providerStatus']
    IndexPath = $indexPath
    IndexSizeBytes = if ($null -ne $indexFile) { $indexFile.Length } else { $null }
    FilesPerSecond = $filesPerSecond
    ChunksPerSecond = $chunksPerSecond
    AverageEmbeddingMilliseconds = $averageEmbeddingMilliseconds
    TimingBreakdown = $timings
    Build = $build
    PreviousIndexedFileCount = [int]$beforeStatus['indexedFileCount']
    PreviousIndexedChunkCount = [int]$beforeStatus['indexedChunkCount']
}

$summaryJson = $summary | ConvertTo-Json -Depth 20
if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    $fullSummaryPath = [System.IO.Path]::GetFullPath($SummaryPath)
    $directory = Split-Path $fullSummaryPath -Parent
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Set-Content -Path $fullSummaryPath -Value $summaryJson -Encoding UTF8
    Write-Host "Summary written to: $fullSummaryPath"
}

Write-Host $summaryJson