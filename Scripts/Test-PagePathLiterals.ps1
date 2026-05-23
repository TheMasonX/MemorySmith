<#
.SYNOPSIS
Validates plain-text Data/Pages markdown path literals in wiki pages.

.DESCRIPTION
Scans markdown files under Data/Pages for plain-text path literals such as
Data/Pages/some-page.md and ensures they still match the current on-disk
layout after page moves/reorganization.

Fails when:
- a path literal uses backslashes, or
- a path literal points to a non-existent markdown file.

If a single unambiguous moved target exists (same filename elsewhere under
Data/Pages), the script prints a suggested replacement.
#>
[CmdletBinding()]
param(
    [string]$PagesRoot,
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path $PSScriptRoot -Parent
}

if ([string]::IsNullOrWhiteSpace($PagesRoot)) {
    $PagesRoot = Join-Path $RepoRoot 'Data\Pages'
}

$resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$resolvedPagesRoot = (Resolve-Path -LiteralPath $PagesRoot).Path

$markdownFiles = Get-ChildItem -LiteralPath $resolvedPagesRoot -Recurse -File -Filter '*.md' |
    Sort-Object -Property FullName

if (-not $markdownFiles) {
    Write-Host "No markdown files found under $resolvedPagesRoot"
    exit 0
}

$knownPagePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$pathsByFileName = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($page in $markdownFiles) {
    $relative = [System.IO.Path]::GetRelativePath($resolvedPagesRoot, $page.FullName)
    $relative = $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/').Replace([System.IO.Path]::AltDirectorySeparatorChar, '/')
    $literalPath = "Data/Pages/$relative"
    $knownPagePaths.Add($literalPath) | Out-Null

    $name = [System.IO.Path]::GetFileName($relative)
    if (-not $pathsByFileName.ContainsKey($name)) {
        $pathsByFileName[$name] = [System.Collections.Generic.List[string]]::new()
    }
    $pathsByFileName[$name].Add($literalPath)
}

$pathLiteralRegex = [regex]'(?i)\bData[\\/]+Pages[\\/]+(?<relative>[A-Za-z0-9._\\/-]+\.md)\b'
$violations = New-Object System.Collections.Generic.List[object]
$checkedLiterals = 0

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

function Add-Violation {
    param(
        [string]$File,
        [int]$Line,
        [string]$Literal,
        [string]$Reason
    )

    $violations.Add([pscustomobject]@{
        File = $File
        Line = $Line
        Literal = $Literal
        Reason = $Reason
    }) | Out-Null
}

foreach ($file in $markdownFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw

    # Ignore fenced blocks and inline code so examples are not treated as live literals.
    $analysisContent = [regex]::Replace($content, '(?s)```.*?```', '')
    $analysisContent = [regex]::Replace($analysisContent, '`[^`]*`', '')

    $matches = $pathLiteralRegex.Matches($analysisContent)
    foreach ($match in $matches) {
        $checkedLiterals++
        $line = Get-LineNumber -Content $analysisContent -Index $match.Index
        $literal = $match.Value
        $relativeRaw = $match.Groups['relative'].Value
        $relativeNormalized = $relativeRaw.Replace('\\', '/')
        $normalizedLiteral = "Data/Pages/$relativeNormalized"

        if ($literal -match '\\') {
            Add-Violation -File $file.FullName -Line $line -Literal $literal -Reason "Use forward slashes. Preferred: $normalizedLiteral"
            continue
        }

        if ($knownPagePaths.Contains($normalizedLiteral)) {
            continue
        }

        $name = [System.IO.Path]::GetFileName($relativeNormalized)
        $suggestions = @()
        if ($pathsByFileName.ContainsKey($name)) {
            $suggestions = @($pathsByFileName[$name])
        }

        if ($suggestions.Count -eq 1) {
            Add-Violation -File $file.FullName -Line $line -Literal $literal -Reason "Path literal does not exist. Did you mean $($suggestions[0])?"
            continue
        }

        if ($suggestions.Count -gt 1) {
            $firstFew = ($suggestions | Sort-Object | Select-Object -First 3) -join ', '
            Add-Violation -File $file.FullName -Line $line -Literal $literal -Reason "Path literal does not exist. Multiple candidates found: $firstFew"
            continue
        }

        Add-Violation -File $file.FullName -Line $line -Literal $literal -Reason 'Path literal does not exist under Data/Pages.'
    }
}

Write-Host "Checked $checkedLiterals page-path literal(s) across $($markdownFiles.Count) files under $resolvedPagesRoot"

if ($violations.Count -eq 0) {
    Write-Host 'PASS: No page-path literal violations found.'
    exit 0
}

Write-Host "FAIL: Found $($violations.Count) page-path literal violation(s)."
foreach ($v in $violations) {
    $relativeFile = [System.IO.Path]::GetRelativePath($resolvedRepoRoot, $v.File)
    Write-Host (" - {0}:{1} -> {2} [{3}]" -f $relativeFile, $v.Line, $v.Literal, $v.Reason)
}

exit 1