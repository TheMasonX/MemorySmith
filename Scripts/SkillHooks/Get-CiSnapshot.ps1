[CmdletBinding()]
param(
    [string]$Branch,
    [string]$Commit,
    [int]$Limit = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$json = gh run list --limit $Limit --json databaseId,name,workflowName,status,conclusion,headBranch,headSha,createdAt,updatedAt 2>$null
if (-not $json) {
    throw 'Unable to read CI runs via gh. Ensure GitHub CLI is authenticated and has repository access.'
}

$runs = $json | ConvertFrom-Json

if ($Branch) {
    $runs = $runs | Where-Object { $_.headBranch -eq $Branch }
}

if ($Commit) {
    $runs = $runs | Where-Object { $_.headSha -like "$Commit*" }
}

$result = [PSCustomObject]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    branch = $Branch
    commit = $Commit
    runCount = @($runs).Count
    runs = @($runs)
}

$result | ConvertTo-Json -Depth 6
