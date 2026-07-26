$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$tasksRoot = Join-Path $repoRoot 'Data/Tasks'

if (-not (Test-Path -LiteralPath $tasksRoot)) {
    throw "Task records directory not found: $tasksRoot"
}

$allowedStatuses = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
@('Backlog', 'Ready', 'InProgress', 'Blocked', 'Rejected', 'Done', 'Archived') | ForEach-Object { [void]$allowedStatuses.Add($_) }
$allowedPriorities = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
@('Critical', 'High', 'Medium', 'Low') | ForEach-Object { [void]$allowedPriorities.Add($_) }
$requiredFields = @('id', 'key', 'title', 'status', 'type', 'priority', 'description', 'createdAtUtc', 'updatedAtUtc', 'revision')

$errors = New-Object 'System.Collections.Generic.List[string]'
$records = New-Object 'System.Collections.Generic.List[object]'

function Test-PageSlug {
    param([string]$Slug)

    if ([string]::IsNullOrWhiteSpace($Slug)) {
        return $false
    }

    try {
        $candidate = [System.Uri]::UnescapeDataString($Slug.Trim())
    }
    catch {
        return $false
    }

    $candidate = $candidate.Replace('\', '/').Trim().Trim('/')
    if ($candidate.StartsWith('pages/', [System.StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring('pages/'.Length).Trim('/')
    }

    if ($candidate.EndsWith('.md', [System.StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring(0, $candidate.Length - 3)
    }

    $segments = $candidate.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Count -eq 0) {
        return $false
    }

    foreach ($segment in $segments) {
        $trimmed = $segment.Trim()
        if ($trimmed -eq '.' -or $trimmed -eq '..' -or $trimmed -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$') {
            return $false
        }
    }

    return $true
}

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
    $priority = [string]$task.priority
    $expectedFileName = if ([string]::IsNullOrWhiteSpace($id)) { $null } else { $id + '.json' }

    foreach ($field in $requiredFields) {
        if ($null -eq $task.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$task.$field)) {
            [void]$errors.Add("$($_.Name): missing required field '$field'")
        }
    }

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

    if (-not [string]::IsNullOrWhiteSpace($priority) -and -not $allowedPriorities.Contains($priority)) {
        [void]$errors.Add("$($_.Name): priority '$priority' is not one of $($allowedPriorities -join ', ')")
    }

    if ($id -notmatch '^tsk-\d{4}-[a-z0-9-]+$') {
        [void]$errors.Add("$($_.Name): id '$id' does not match tsk-0000-slug format")
    }

    if ($null -ne $task.labels) {
        foreach ($label in $task.labels) {
            if ([string]$label -match '^(?i:p\d+)$') {
                [void]$errors.Add("$($_.Name): priority label '$label' is prohibited; use priority field")
            }
        }
    }

    if ($null -ne $task.linkedPages) {
        foreach ($linkedPage in $task.linkedPages) {
            $slug = [string]$linkedPage
            if (-not (Test-PageSlug -Slug $slug)) {
                [void]$errors.Add("$($_.Name): linkedPages entry '$slug' is not a safe page slug")
            }
        }
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