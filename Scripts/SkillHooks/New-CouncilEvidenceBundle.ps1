[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Decision,
    [string[]]$PageLinks = @(),
    [string[]]$MemoryIds = @(),
    [string[]]$CodePaths = @(),
    [string[]]$ValidationCommands = @(),
    [string[]]$ValidationResults = @(),
    [string[]]$OpenQuestions = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Council Evidence Bundle')
$lines.Add('')
$lines.Add('## Decision Topic')
$lines.Add($Decision)
$lines.Add('')
$lines.Add('## Evidence Reviewed')

if ($PageLinks.Count -gt 0) {
    $lines.Add('- Pages:')
    foreach ($page in $PageLinks) {
        $lines.Add('  - ' + $page)
    }
}

if ($MemoryIds.Count -gt 0) {
    $lines.Add('- Memory records:')
    foreach ($id in $MemoryIds) {
        $lines.Add('  - `' + $id + '`')
    }
}

if ($CodePaths.Count -gt 0) {
    $lines.Add('- Code evidence:')
    foreach ($path in $CodePaths) {
        $lines.Add('  - `' + $path + '`')
    }
}

if ($ValidationCommands.Count -gt 0) {
    $lines.Add('- Validation commands:')
    foreach ($cmd in $ValidationCommands) {
        $lines.Add('  - `' + $cmd + '`')
    }
}

if ($ValidationResults.Count -gt 0) {
    $lines.Add('- Validation results:')
    foreach ($result in $ValidationResults) {
        $lines.Add('  - ' + $result)
    }
}

if ($OpenQuestions.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Open Questions')
    foreach ($q in $OpenQuestions) {
        $lines.Add('- ' + $q)
    }
}

$bundleMarkdown = [string]::Join("`n", $lines)

$result = [PSCustomObject]@{
    decision = $Decision
    bundleMarkdown = $bundleMarkdown
    recommendedMcpReads = @(
        'mcp_memorysmithwi_memorysmith_context_pack',
        'mcp_memorysmithwi_memorysmith_hybrid_search',
        'mcp_memorysmithwi_memorysmith_get'
    )
}

$result | ConvertTo-Json -Depth 6
