[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int]$PullNumber,
    [int]$PollSeconds = 60,
    [int]$TimeoutMinutes = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PollSeconds -lt 10) {
    throw 'PollSeconds must be at least 10 seconds.'
}

if ($TimeoutMinutes -lt 1) {
    throw 'TimeoutMinutes must be at least 1.'
}

$start = Get-Date
$deadline = $start.AddMinutes($TimeoutMinutes)
$polls = @()

while ((Get-Date) -lt $deadline) {
    $raw = gh pr view $PullNumber --json number,title,state,isDraft,reviewDecision,mergeStateStatus,latestReviews,updatedAt 2>$null
    if (-not $raw) {
        throw "Unable to load PR #$PullNumber via gh."
    }

    $pr = $raw | ConvertFrom-Json
    $poll = [PSCustomObject]@{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        state = $pr.state
        isDraft = [bool]$pr.isDraft
        reviewDecision = $pr.reviewDecision
        mergeStateStatus = $pr.mergeStateStatus
        latestReviewCount = @($pr.latestReviews).Count
    }
    $polls += $poll

    $isReady = ($pr.state -eq 'OPEN' -and -not $pr.isDraft -and ($pr.reviewDecision -eq 'APPROVED' -or $pr.reviewDecision -eq ''))
    $isMerged = ($pr.state -eq 'MERGED')

    if ($isReady -or $isMerged) {
        $result = [PSCustomObject]@{
            pullNumber = $pr.number
            title = $pr.title
            timedOut = $false
            ready = $isReady
            merged = $isMerged
            startedUtc = $start.ToUniversalTime().ToString('o')
            finishedUtc = [DateTime]::UtcNow.ToString('o')
            polls = $polls
            final = $poll
        }
        $result | ConvertTo-Json -Depth 8
        exit 0
    }

    Start-Sleep -Seconds $PollSeconds
}

$finalRaw = gh pr view $PullNumber --json number,title,state,isDraft,reviewDecision,mergeStateStatus,latestReviews,updatedAt 2>$null
$finalPr = $finalRaw | ConvertFrom-Json
$finalPoll = [PSCustomObject]@{
    timestampUtc = [DateTime]::UtcNow.ToString('o')
    state = $finalPr.state
    isDraft = [bool]$finalPr.isDraft
    reviewDecision = $finalPr.reviewDecision
    mergeStateStatus = $finalPr.mergeStateStatus
    latestReviewCount = @($finalPr.latestReviews).Count
}

$result = [PSCustomObject]@{
    pullNumber = $finalPr.number
    title = $finalPr.title
    timedOut = $true
    ready = $false
    merged = ($finalPr.state -eq 'MERGED')
    startedUtc = $start.ToUniversalTime().ToString('o')
    finishedUtc = [DateTime]::UtcNow.ToString('o')
    polls = $polls
    final = $finalPoll
}

$result | ConvertTo-Json -Depth 8
