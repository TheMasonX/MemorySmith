$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$tasksRoot = Join-Path $repoRoot 'Data/Tasks'

if (-not (Test-Path -LiteralPath $tasksRoot)) {
    throw "Task records directory not found: $tasksRoot"
}

$allowedStatuses = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
@('Backlog', 'Ready', 'InProgress', 'Blocked', 'Done', 'Archived') | ForEach-Object { [void]$allowedStatuses.Add($_) }

$errors = New-Object 'System.Collections.Generic.List[string]'
$records = New-Object 'System.Collections.Generic.List[object]'

Get-ChildItem -LiteralPath $tasksRoot -Filter '*.json' -File | Sort-Object Name | ForEach-Object {
    try {
        $task = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    }
    catch {
        [void]$errors.Add("$($_.Name): invalid JSON: $($_.Exception.Message)")
        return
    }

    $id = [string]$task.id
    $key = [string]$task.key
    $title = [string]$task.title
    $status = [string]$task.status
    $expectedFileName = if ([string]::IsNullOrWhiteSpace($id)) { $null } else { $id + '.json' }

    if ([string]::IsNullOrWhiteSpace($id)) {
        [void]$errors.Add("$($_.Name): missing id")
    }
    elseif (-not [string]::Equals($_.Name, $expectedFileName, [System.StringComparison]::OrdinalIgnoreCase)) {
        [void]$errors.Add("$($_.Name): file name does not match id '$id'")
    }

    if ([string]::IsNullOrWhiteSpace($key)) {
        [void]$errors.Add("$($_.Name): missing key")
    }
    elseif ($key -notmatch '^TSK-\d{4,}$') {
        [void]$errors.Add("$($_.Name): key '$key' does not match TSK-0000 format")
    }

    if ([string]::IsNullOrWhiteSpace($title)) {
        [void]$errors.Add("$($_.Name): missing title")
    }

    if ([string]::IsNullOrWhiteSpace($status)) {
        [void]$errors.Add("$($_.Name): missing status")
    }
    elseif (-not $allowedStatuses.Contains($status)) {
        [void]$errors.Add("$($_.Name): status '$status' is not one of $($allowedStatuses -join ', ')")
    }

    [void]$records.Add([pscustomobject]@{
        FileName = $_.Name
        Id = $id
        NormalizedId = $id.ToLowerInvariant()
        Key = $key
        NormalizedKey = $key.ToUpperInvariant()
    })
}

$records | Where-Object { -not [string]::IsNullOrWhiteSpace($_.NormalizedId) } | Group-Object NormalizedId | Where-Object Count -gt 1 | ForEach-Object {
    $files = ($_.Group | Sort-Object FileName | ForEach-Object FileName) -join ', '
    [void]$errors.Add("Duplicate task id '$($_.Name)' in $files")
}

$records | Where-Object { -not [string]::IsNullOrWhiteSpace($_.NormalizedKey) } | Group-Object NormalizedKey | Where-Object Count -gt 1 | ForEach-Object {
    $files = ($_.Group | Sort-Object FileName | ForEach-Object FileName) -join ', '
    [void]$errors.Add("Duplicate task key '$($_.Name)' in $files")
}

if ($errors.Count -gt 0) {
    Write-Host "FAIL: Task record validation found $($errors.Count) issue(s)." -ForegroundColor Red
    $errors | ForEach-Object { Write-Host (" - " + $_) -ForegroundColor Red }
    throw 'Task record validation failed.'
}

Write-Host "PASS: Checked $($records.Count) task record(s); keys and ids are unique."