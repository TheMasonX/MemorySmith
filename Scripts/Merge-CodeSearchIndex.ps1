#Requires -Version 7.0
<#
.SYNOPSIS
    Merges a code-search index shard database into the main MemorySmith index via the MCP API.

.DESCRIPTION
    Calls the memorysmith_code_search_merge_shard MCP tool to merge chunks from a shard SQLite
    database file into the main code-search index. Reports inserted, updated, and skipped chunk
    counts when the merge completes.

.PARAMETER ShardPath
    Absolute path to the shard SQLite database file to merge.

.PARAMETER BaseUrl
    Base URL of the MemorySmith service. Defaults to http://localhost:5089.

.PARAMETER ApiKey
    API key for authentication. Reads from MEMORYSMITH_API_KEY environment variable if omitted.

.PARAMETER PreferNewer
    When $true (default), overwrite existing chunks if the shard has a newer IndexedAtUtc
    timestamp. When $false, only insert new chunks; never update existing ones.

.PARAMETER SkipCertificateCheck
    Skip TLS certificate validation (useful for local HTTPS with self-signed certs).

.EXAMPLE
    .\Merge-CodeSearchIndex.ps1 -ShardPath "C:\builds\ci-shard.db"

.EXAMPLE
    .\Merge-CodeSearchIndex.ps1 -ShardPath "/tmp/shard.db" -BaseUrl "https://myserver:5090" -PreferNewer $false
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ShardPath,

    [string]$BaseUrl = "http://localhost:5089",

    [string]$ApiKey = "",

    [bool]$PreferNewer = $true,

    [switch]$SkipCertificateCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Resolve API key
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = $env:MEMORYSMITH_API_KEY
}

# Validate shard path existence before calling the service
if (-not [System.IO.Path]::IsPathRooted($ShardPath)) {
    $ShardPath = [System.IO.Path]::GetFullPath($ShardPath)
}
if (-not (Test-Path -LiteralPath $ShardPath -PathType Leaf)) {
    Write-Error "Shard file not found: $ShardPath"
    exit 1
}

$mcpUrl = "$($BaseUrl.TrimEnd('/'))/mcp"

$headers = @{ "Content-Type" = "application/json" }
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $headers["Authorization"] = "Bearer $ApiKey"
}

$requestBody = @{
    jsonrpc = "2.0"
    id      = [guid]::NewGuid().ToString("N")
    method  = "tools/call"
    params  = @{
        name      = "memorysmith_code_search_merge_shard"
        arguments = @{
            shardPath   = $ShardPath
            preferNewer = $PreferNewer
        }
    }
} | ConvertTo-Json -Depth 5

Write-Host "Merging shard: $ShardPath"
Write-Host "Target:        $mcpUrl"
Write-Host "PreferNewer:   $PreferNewer"
Write-Host ""

$invokeParams = @{
    Uri     = $mcpUrl
    Method  = "POST"
    Headers = $headers
    Body    = $requestBody
}
if ($SkipCertificateCheck) {
    $invokeParams["SkipCertificateCheck"] = $true
}

$response = Invoke-RestMethod @invokeParams

# MCP response contains result.content[0].text with the JSON payload
$resultText = $response.result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1 -ExpandProperty text
if ([string]::IsNullOrWhiteSpace($resultText)) {
    Write-Error "Empty or unexpected response from MCP tool."
    exit 1
}

$result = $resultText | ConvertFrom-Json

if ($response.result.isError -eq $true) {
    Write-Error "Merge failed: $resultText"
    exit 1
}

Write-Host "Merge completed successfully."
Write-Host "  Shard file:       $($result.shardPath)"
Write-Host "  Total shard chunks: $($result.totalShardChunkCount)"
Write-Host "  Inserted:         $($result.insertedChunkCount)"
Write-Host "  Updated:          $($result.updatedChunkCount)"
Write-Host "  Skipped:          $($result.skippedChunkCount)"
Write-Host "  Elapsed:          $($result.elapsedMilliseconds) ms"
