$ErrorActionPreference = 'Stop'

Set-Location (Split-Path -Parent $PSScriptRoot)

$tasksFile = 'Data/Pages/workbench/tasks.md'
$tasksRoot = 'Data/Tasks'
$eventsPath = 'Data/Events/tasks.activity.jsonl'

New-Item -ItemType Directory -Force -Path $tasksRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $eventsPath) | Out-Null

$lines = Get-Content $tasksFile
$openRows = @()
$inTable = $false
foreach ($line in $lines) {
    if ($line -match '^##\s+Current Priorities') {
        $inTable = $true
        continue
    }

    if ($inTable -and $line -match '^##\s+') {
        break
    }

    if (-not $inTable) {
        continue
    }

    if ($line -match '^\|\s*Open\s*\|') {
        $parts = $line -split '\|'
        if ($parts.Count -ge 6) {
            $openRows += [pscustomobject]@{
                Owner = $parts[2].Trim()
                Task = $parts[3].Trim()
                Notes = $parts[4].Trim()
                Screenshot = $parts[5].Trim()
            }
        }
    }
}

$existing = @()
Get-ChildItem -Path $tasksRoot -Filter '*.json' -File -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $existing += (Get-Content $_.FullName -Raw | ConvertFrom-Json)
    }
    catch {
    }
}

$maxKey = 0
foreach ($task in $existing) {
    if ($task.Key -match '^TSK-(\d{4,})$') {
        $candidate = [int]$Matches[1]
        if ($candidate -gt $maxKey) {
            $maxKey = $candidate
        }
    }
}

$existingTitles = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($task in $existing) {
    if (-not [string]::IsNullOrWhiteSpace($task.Title)) {
        [void]$existingTitles.Add($task.Title)
    }
}

function Get-Slug([string]$value) {
    $slug = $value.ToLowerInvariant()
    $slug = [regex]::Replace($slug, '[^a-z0-9]+', '-')
    $slug = $slug.Trim('-')
    if ([string]::IsNullOrWhiteSpace($slug)) {
        return 'task'
    }

    return $slug
}

$created = 0
$skippedDuplicates = 0
$warnings = 0

foreach ($row in $openRows) {
    if ([string]::IsNullOrWhiteSpace($row.Task)) {
        continue
    }

    if ($existingTitles.Contains($row.Task)) {
        $skippedDuplicates++
        continue
    }

    $maxKey++
    $key = ('TSK-{0:0000}' -f $maxKey)
    $id = ($key.ToLowerInvariant() + '-' + (Get-Slug $row.Task))
    $now = [DateTime]::UtcNow

    $comments = @()
    if (-not [string]::IsNullOrWhiteSpace($row.Notes) -and $row.Notes -ne 'Pending') {
        $comments += [ordered]@{
            Id = ('c-' + [Guid]::NewGuid().ToString('N'))
            Author = 'tracker-import'
            Body = $row.Notes
            CreatedAtUtc = $now
        }
    }

    $summaryText = $row.Task
    if ($comments.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($comments[0].Body)) {
        $summaryText = $comments[0].Body
    }

    $importNote = 'Imported from Data/Pages/workbench/tasks.md'
    $description = "$summaryText`n`n$importNote"

    $attachments = @()
    if (-not [string]::IsNullOrWhiteSpace($row.Screenshot) -and $row.Screenshot -ne 'Pending') {
        $attachments += [ordered]@{
            Id = ('a-' + [Guid]::NewGuid().ToString('N'))
            Name = 'Tracker screenshot'
            Kind = 'image'
            Uri = $row.Screenshot
            AddedAtUtc = $now
        }
    }

    $owner = $row.Owner
    if ([string]::IsNullOrWhiteSpace($owner)) {
        $owner = 'Unassigned'
    }

    $taskPayload = [ordered]@{
        Id = $id
        Key = $key
        Title = $row.Task
        Description = $description
        Type = 'Task'
        Status = 'Backlog'
        Priority = 'Medium'
        AssigneeMode = 'Custom'
        AssigneeDirectoryId = $null
        AssigneeCustomText = $owner
        Reporter = 'tracker-import'
        Labels = @('tracker-import', 'workbench', 'future')
        Attachments = $attachments
        ExternalLinks = @()
        LinkedPages = @('workbench/tasks')
        Comments = $comments
        EpicId = $null
        ParentId = $null
        DueDateUtc = $null
        CreatedAtUtc = $now
        UpdatedAtUtc = $now
        CompletedAtUtc = $null
        Revision = 1
        IsArchived = $false
    }

    $taskJson = $taskPayload | ConvertTo-Json -Depth 12
    Set-Content -Path (Join-Path $tasksRoot ($id + '.json')) -Value ($taskJson + [Environment]::NewLine) -Encoding UTF8

    $createdEvent = [ordered]@{
        TaskId = $id
        Action = 'created'
        Actor = 'tracker-import'
        Note = $null
        OccurredAtUtc = $now
    } | ConvertTo-Json -Compress

    $pageLinkEvent = [ordered]@{
        TaskId = $id
        Action = 'page_link_added'
        Actor = 'tracker-import'
        Note = 'workbench/tasks'
        OccurredAtUtc = $now
    } | ConvertTo-Json -Compress

    Add-Content -Path $eventsPath -Value $createdEvent -Encoding UTF8
    Add-Content -Path $eventsPath -Value $pageLinkEvent -Encoding UTF8

    if (-not (Test-Path 'Data/Pages/workbench/tasks.md')) {
        $warningEvent = [ordered]@{
            TaskId = $id
            Action = 'page_link_warning'
            Actor = 'tracker-import'
            Note = 'Linked page not found: workbench/tasks'
            OccurredAtUtc = $now
        } | ConvertTo-Json -Compress

        Add-Content -Path $eventsPath -Value $warningEvent -Encoding UTF8
        $warnings++
    }

    [void]$existingTitles.Add($row.Task)
    $created++
}

Write-Host ("IMPORT_SUMMARY openRows=$($openRows.Count) created=$created skippedDuplicates=$skippedDuplicates warnings=$warnings")
