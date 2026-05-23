<#
.SYNOPSIS
Validates markdown links across Data/Pages.

.DESCRIPTION
Checks in-repo markdown page links and enforces relative .md link style for
cross-tool compatibility (for example Obsidian).

By default this script validates non-image markdown links in all *.md files
under Data/Pages and fails when:
- a local markdown link is not relative, or
- a local markdown link target does not exist.
#>
[CmdletBinding()]
param(
    [string]$PagesRoot,
    [switch]$RequireRelativeMarkdownLinks = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($PagesRoot)) {
    $PagesRoot = Join-Path $repoRoot 'Data\Pages'
}

$resolvedPagesRoot = (Resolve-Path -LiteralPath $PagesRoot).Path
$markdownFiles = Get-ChildItem -LiteralPath $resolvedPagesRoot -Recurse -File -Filter '*.md' |
    Sort-Object -Property FullName

if (-not $markdownFiles) {
    Write-Host "No markdown files found under $resolvedPagesRoot"
    exit 0
}

$linkRegex = [regex]'(?<!!)!?\[[^\]]+\]\((?<target>[^)]+)\)'
$violations = New-Object System.Collections.Generic.List[object]
$checkedLinks = 0

function Add-Violation {
    param(
        [string]$File,
        [int]$Line,
        [string]$Target,
        [string]$Reason
    )

    $violations.Add([pscustomobject]@{
        File = $File
        Line = $Line
        Target = $Target
        Reason = $Reason
    }) | Out-Null
}

function Get-LineNumber {
    param(
        [string]$Content,
        [int]$Index
    )

    if ($Index -le 0) {
        return 1
    }

    $prefix = $Content.Substring(0, [Math]::Min($Index, $Content.Length))
    return ($prefix -split "`n").Count
}

foreach ($file in $markdownFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw

    # Ignore fenced code blocks and inline code spans so documentation examples
    # do not get treated as live links.
    $analysisContent = [regex]::Replace($content, '(?s)```.*?```', '')
    $analysisContent = [regex]::Replace($analysisContent, '`[^`]*`', '')

    $matches = $linkRegex.Matches($analysisContent)

    foreach ($match in $matches) {
        $targetRaw = $match.Groups['target'].Value.Trim()

        if ([string]::IsNullOrWhiteSpace($targetRaw)) {
            continue
        }

        $checkedLinks++

        # Strip optional title part: (path "title")
        if ($targetRaw -match '^(?<path><[^>]+>|[^\s]+)(\s+"[^"]*")?$') {
            $targetRaw = $Matches['path']
        }

        $target = $targetRaw.Trim('<', '>').Trim()

        if ([string]::IsNullOrWhiteSpace($target)) {
            continue
        }

        # Ignore anchors and explicit schemes (http:, mailto:, memory:, page:, etc)
        if ($target.StartsWith('#') -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
            continue
        }

        $targetNoQuery = ($target -split '\?')[0]
        $targetPathOnly = ($targetNoQuery -split '#')[0]

        # Only enforce markdown page-link policy here.
        if (-not $targetPathOnly.EndsWith('.md', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $line = Get-LineNumber -Content $analysisContent -Index $match.Index
        $isAbsolute = $targetPathOnly.StartsWith('/') -or $targetPathOnly.StartsWith('\\')

        if ($RequireRelativeMarkdownLinks -and $isAbsolute) {
            Add-Violation -File $file.FullName -Line $line -Target $target -Reason 'Markdown page link must be relative, not absolute.'
            continue
        }

        if ($targetPathOnly.Contains('..\') -or $targetPathOnly.Contains('\\')) {
            Add-Violation -File $file.FullName -Line $line -Target $target -Reason 'Use forward slashes in markdown links.'
            continue
        }

        if ($isAbsolute) {
            $resolvedTarget = Join-Path $resolvedPagesRoot $targetPathOnly.TrimStart('/', '\\')
        }
        else {
            $resolvedTarget = Join-Path $file.DirectoryName $targetPathOnly
        }

        $normalizedTarget = [System.IO.Path]::GetFullPath($resolvedTarget)
        if (-not $normalizedTarget.StartsWith($resolvedPagesRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            # This checker validates page-to-page links only.
            # Links to markdown outside Data/Pages are ignored.
            continue
        }

        if (-not (Test-Path -LiteralPath $normalizedTarget -PathType Leaf)) {
            Add-Violation -File $file.FullName -Line $line -Target $target -Reason 'Linked markdown file does not exist.'
            continue
        }
    }
}

Write-Host "Checked $checkedLinks markdown links across $($markdownFiles.Count) files under $resolvedPagesRoot"

if ($violations.Count -eq 0) {
    Write-Host 'PASS: No page-link violations found.'
    exit 0
}

Write-Host "FAIL: Found $($violations.Count) page-link violation(s)."
foreach ($v in $violations) {
    $relativeFile = [System.IO.Path]::GetRelativePath($repoRoot, $v.File)
    Write-Host (" - {0}:{1} -> {2} [{3}]" -f $relativeFile, $v.Line, $v.Target, $v.Reason)
}

exit 1
