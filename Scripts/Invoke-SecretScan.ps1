#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Scans staged git changes for potential secrets (API keys, tokens, passwords, etc.)
    and blocks the commit if any are found.

.DESCRIPTION
    This script is designed to run as a git pre-commit hook. It inspects all staged
    file changes (additions and modifications) for patterns matching common secret
    formats. If secrets are detected, the commit is aborted with a detailed report.

    Usage (install as pre-commit hook):
        Copy this file to .git/hooks/pre-commit

    Integration with CI:
        Invoke-SecretScan.ps1 -ScanAll $true

.PARAMETER ScanAll
    When $true, scans all files in the repository instead of just staged changes.
    Used in CI workflows for full-repo scans.

.PARAMETER ReportPath
    Path to write the scan report (JSON). Default: no file written.

.EXAMPLE
    # Run as pre-commit hook (default)
    .\Scripts\Invoke-SecretScan.ps1

    # Full repo scan (CI)
    .\Scripts\Invoke-SecretScan.ps1 -ScanAll $true -ReportPath artifacts/secret-scan-report.json

.NOTES
    Sprint 60 (TSK-0349): Secret scanning pre-commit hook.
#>

param(
    [switch]$ScanAll,
    [string]$ReportPath = ""
)

# ── Secret patterns ────────────────────────────────────────────────────────────
# These regex patterns detect high-entropy / likely-secret strings.
# False positives are possible; reviewed patterns are listed in known-false-positives below.

$patterns = @(
    # API keys / tokens
    @{ Name = "API Key (generic)";        Regex = '(?i)(api[_-]?key|api[_-]?secret|api[_-]?token)\s*[:=]\s*["'']?[A-Za-z0-9_\-]{16,}["'']?'; Severity = "High" },
    @{ Name = "Bearer/Authorization token"; Regex = '(?i)(bearer|authorization)\s+[A-Za-z0-9_\-\.]{20,}'; Severity = "High" },
    @{ Name = "JWT token";                 Regex = 'eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}'; Severity = "High" },
    @{ Name = "AWS Access Key";            Regex = '(?i)AKIA[0-9A-Z]{16}'; Severity = "Critical" },
    @{ Name = "AWS Secret Key";            Regex = '(?i)(aws[_-]?secret|secret[_-]?access[_-]?key)\s*[:=]\s*["'']?[A-Za-z0-9\/+=]{40}["'']?'; Severity = "Critical" },
    @{ Name = "GitHub Token / PAT";        Regex = '(?i)(ghp_|gho_|ghu_|ghs_|ghr_)[A-Za-z0-9_\-]{36,}'; Severity = "Critical" },
    @{ Name = "GitHub App Token";          Regex = '(?i)(ghx_)[A-Za-z0-9_\-]{36,}'; Severity = "Critical" },
    @{ Name = "NuGet API Key";             Regex = '(?i)(nuget[_-]?api[_-]?key)\s*[:=]\s*["'']?[A-Za-z0-9_\-]{20,}["'']?'; Severity = "High" },
    @{ Name = "Slack Bot Token";           Regex = '(?i)xox[bpras]-\d+-[A-Za-z0-9\-]{20,}'; Severity = "Critical" },
    @{ Name = "Generic connection string"; Regex = '(?i)(connection[_-]?string|connstr)\s*[:=]\s*["'']?.+?["'']?'; Severity = "High" },
    @{ Name = "Private SSH key";           Regex = '-----BEGIN\s+(RSA|EC|DSA|OPENSSH)\s+PRIVATE\s+KEY-----'; Severity = "Critical" },
    @{ Name = "Password field (config)";   Regex = '(?i)(password|pwd|passwd)\s*[:=]\s*["'']?[^"''\s]{4,}["'']?'; Severity = "High" }
)

# ── Known false positive patterns (allowed in codebase) ────────────────────────
$falsePositivePatterns = @(
    '(?i)github[_-]?token.*\{',                        # Template / placeholder
    '(?i)example\.com',                                 # Example domains
    '(?i)your[_-]?api[_-]?key',                         # Placeholder text
    '(?i)password.*placeholder',                        # Documentation placeholders
    'password.*P@ssw0rd',                               # Common example password
    'password.*REPLACE_ME'                              # Template marker
)

# ── Files to skip ──────────────────────────────────────────────────────────────
$skipExtensions = @('.md', '.txt', '.html', '.css')  # Doc files scanned but with lower priority
$skipPaths = @(
    '.git/',
    'node_modules/',
    'bin/',
    'obj/',
    'artifacts/',
    'logs/',
    '.gitattributes',
    '.gitignore',
    'LICENSE',
    'Scripts/Invoke-SecretScan.ps1'  # Self-exclusion (contains patterns as examples)
)

# ── Helper: check if path should be skipped ────────────────────────────────────
function Test-ShouldSkipPath($path) {
    foreach ($skip in $skipPaths) {
        if ($path -like "*$skip*") { return $true }
    }
    return $false
}

# ── Helper: check if content matches a false-positive pattern ──────────────────
function Test-IsFalsePositive($content) {
    foreach ($fp in $falsePositivePatterns) {
        if ($content -match $fp) { return $true }
    }
    return $false
}

# ── Main scanning logic ────────────────────────────────────────────────────────
function Invoke-SecretScan {
    $findings = @()
    $filesToScan = @()

    if ($ScanAll) {
        # Full repo scan
        $allFiles = Get-ChildItem -Recurse -File | Where-Object {
            -not (Test-ShouldSkipPath $_.FullName)
        }
        $filesToScan = $allFiles
    }
    else {
        # Staged changes only (pre-commit mode)
        $staged = git diff --cached --name-only --diff-filter=ACMRT
        foreach ($file in $staged) {
            if (-not (Test-ShouldSkipPath $file) -and (Test-Path $file)) {
                $filesToScan += Get-Item $file
            }
        }
    }

    $fileCount = 0
    foreach ($file in $filesToScan) {
        if ($fileCount -ge 500) { break }  # Safety limit
        $fileCount++

        try {
            $content = if ($ScanAll) {
                Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
            } else {
                git show ":$($file)" 2>$null
            }

            if ([string]::IsNullOrEmpty($content)) { continue }

            foreach ($pattern in $patterns) {
                $matches = [regex]::Matches($content, $pattern.Regex)
                foreach ($match in $matches) {
                    # Get context: 2 lines before and after
                    $lines = $content -split "`n"
                    $lineIndex = 0
                    $matchLine = -1
                    for ($i = 0; $i -lt $lines.Length; $i++) {
                        if ($lines[$i] -match [regex]::Escape($match.Value.Substring(0, [Math]::Min(20, $match.Value.Length)))) {
                            $matchLine = $i + 1
                            break
                        }
                    }

                    $lineContent = if ($matchLine -gt 0 -and $matchLine -le $lines.Length) { $lines[$matchLine - 1].Trim() } else { "" }

                    if (Test-IsFalsePositive $lineContent) { continue }

                    $finding = @{
                        File     = if ($ScanAll) { $file.FullName } else { $file }
                        Line     = $matchLine
                        Pattern  = $pattern.Name
                        Severity = $pattern.Severity
                        Snippet  = $match.Value.Substring(0, [Math]::Min(80, $match.Value.Length))
                    }
                    $findings += $finding
                }
            }
        }
        catch {
            # Skip files that can't be read
        }
    }

    return $findings
}

# ── Report output ──────────────────────────────────────────────────────────────
$findings = Invoke-SecretScan

if ($ReportPath) {
    $reportDir = Split-Path $ReportPath -Parent
    if ($reportDir -and -not (Test-Path $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    $findings | ConvertTo-Json -Depth 3 | Set-Content $ReportPath
}

if ($findings.Count -eq 0) {
    if (-not $ScanAll) {
        Write-Host "✅ Secret scan passed — no secrets detected in staged changes." -ForegroundColor Green
    }
    return $true
}

# Report findings
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Red
Write-Host "║  ❌ SECRET SCAN FAILED — Potential secrets detected        ║" -ForegroundColor Red
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Red
Write-Host ""

$grouped = $findings | Group-Object Severity
foreach ($group in $grouped) {
    $color = switch ($group.Name) {
        "Critical" { "Red" }
        "High"     { "Yellow" }
        default    { "Gray" }
    }
    Write-Host "[$($group.Name)] $($group.Count) finding(s):" -ForegroundColor $color
    foreach ($f in $group.Group) {
        Write-Host "  📄 $($f.File):$($f.Line)" -ForegroundColor $color
        Write-Host "     Pattern: $($f.Pattern)" -ForegroundColor $color
        Write-Host "     Snippet: $($f.Snippet)" -ForegroundColor $color
    }
    Write-Host ""
}

Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Red
Write-Host "║  Commit BLOCKED. Remove secrets before committing.          ║" -ForegroundColor Red
Write-Host "║  Use 'git diff --cached' to review staged changes.          ║" -ForegroundColor Red
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Red

# Exit with failure in pre-commit mode
if (-not $ScanAll) {
    exit 1
}

return $false
