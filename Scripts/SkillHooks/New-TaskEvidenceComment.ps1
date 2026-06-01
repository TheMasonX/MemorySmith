[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TaskId,
    [Parameter(Mandatory = $true)]
    [string]$Summary,
    [string[]]$ChangedFiles = @(),
    [string[]]$ValidationCommands = @(),
    [string[]]$ValidationResults = @(),
    [string]$Confidence,
    [string[]]$OpenQuestions = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('## Evidence Update')
$lines.Add('')
$lines.Add('- Summary: ' + $Summary)

if ($ChangedFiles.Count -gt 0) {
    $lines.Add('- Changed files:')
    foreach ($file in $ChangedFiles) {
        $lines.Add('  - `' + $file + '`')
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

if ($Confidence) {
    $lines.Add('- Confidence: ' + $Confidence)
}

if ($OpenQuestions.Count -gt 0) {
    $lines.Add('- Open questions:')
    foreach ($question in $OpenQuestions) {
        $lines.Add('  - ' + $question)
    }
}

$body = [string]::Join("`n", $lines)

$result = [PSCustomObject]@{
    taskId = $TaskId
    body = $body
    mcpTool = 'mcp_memorysmithwi_memorysmith_task_add_comment'
    suggestedArgs = [PSCustomObject]@{
        idOrKey = $TaskId
        body = $body
    }
}

$result | ConvertTo-Json -Depth 6
