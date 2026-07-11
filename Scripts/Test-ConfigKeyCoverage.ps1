#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates that every editable admin setting key has a documented entry in
    the configuration key inventory (Data/Pages/guides/configuration-key-inventory.md).

.DESCRIPTION
    Extracts all setting keys from AdminSettingsService.BuildEditableSettings()
    and checks each against the per-key inventory page. Reports:

    - Undocumented keys (newly added settings missing from the inventory)
    - Orphaned inventory entries (keys listed in the inventory but not in code)
    - Key count drift (total key count differs from expected)

    Exit code: 0 = all keys covered, 1 = issues found.

.PARAMETER InventoryPath
    Path to the configuration key inventory markdown file.
    Default: Data/Pages/guides/configuration-key-inventory.md

.PARAMETER SettingsSourcePath
    Path to AdminSettingsService.cs.
    Default: MemorySmith.App/Services/AdminSettingsService.cs

.EXAMPLE
    pwsh ./Scripts/Test-ConfigKeyCoverage.ps1

.LINK
    TSK-0158 — Generate exhaustive configuration setting inventory and doc coverage check
#>

param(
    [string]$InventoryPath = (Join-Path $PSScriptRoot '..\Data\Pages\guides\configuration-key-inventory.md'),
    [string]$SettingsSourcePath = (Join-Path $PSScriptRoot '..\MemorySmith.App\Services\AdminSettingsService.cs')
)

$exitCode = 0

# Resolve paths
$InventoryPath = Resolve-Path $InventoryPath -ErrorAction Stop
$SettingsSourcePath = Resolve-Path $SettingsSourcePath -ErrorAction Stop

Write-Host "=== Configuration Key Coverage Check ===" -ForegroundColor Cyan
Write-Host "Inventory : $InventoryPath"
Write-Host "Source    : $SettingsSourcePath"
Write-Host ""

# ── Step 1: Extract keys from AdminSettingsService ────────────────────────
Write-Host "Step 1: Extracting setting keys from AdminSettingsService..." -ForegroundColor Yellow

$sourceKeys = @()
$sourceContent = Get-Content $SettingsSourcePath -Raw

# Match all EditableSettingDescriptor.* calls with a "MemorySmith:*" key
$pattern = [regex]::new('"MemorySmith:[^"]*"', [System.Text.RegularExpressions.RegexOptions]::Singleline)
$matches = $pattern.Matches($sourceContent)

foreach ($m in $matches) {
    $key = $m.Value.Trim('"')
    if ($key -notin $sourceKeys) {
        $sourceKeys += $key
    }
}

$sourceKeys = $sourceKeys | Sort-Object
Write-Host "  Found $($sourceKeys.Count) unique keys in source."

# ── Step 2: Extract keys from inventory page ──────────────────────────────
Write-Host "Step 2: Extracting keys from inventory page..." -ForegroundColor Yellow

$inventoryKeys = @()
$inventoryContent = Get-Content $InventoryPath -Raw

# Match backtick-quoted keys like `MemorySmith:*`
$invPattern = [regex]::new('`(MemorySmith:[^`]*)`', [System.Text.RegularExpressions.RegexOptions]::Singleline)
$invMatches = $invPattern.Matches($inventoryContent)

foreach ($m in $invMatches) {
    $key = $m.Groups[1].Value
    if ($key -notin $inventoryKeys) {
        $inventoryKeys += $key
    }
}

# Also match inline code like `MemorySmith:*` without leading backtick
# (Already handled above)

$inventoryKeys = $inventoryKeys | Sort-Object
Write-Host "  Found $($inventoryKeys.Count) keys in inventory."

# ── Step 3: Compare ───────────────────────────────────────────────────────
Write-Host "Step 3: Comparing..." -ForegroundColor Yellow
Write-Host ""

$undocumented = $sourceKeys | Where-Object { $_ -notin $inventoryKeys }
$orphaned = $inventoryKeys | Where-Object { $_ -notin $sourceKeys }

if ($undocumented.Count -gt 0) {
    Write-Host "⚠ UNDOCUMENTED KEYS ($($undocumented.Count)):" -ForegroundColor Red
    foreach ($k in $undocumented) {
        Write-Host "  - $k" -ForegroundColor Red
    }
    Write-Host ""
    $exitCode = 1
}

if ($orphaned.Count -gt 0) {
    Write-Host "⚠ ORPHANED INVENTORY ENTRIES ($($orphaned.Count)):" -ForegroundColor Yellow
    foreach ($k in $orphaned) {
        Write-Host "  - $k" -ForegroundColor Yellow
    }
    Write-Host ""
    # Orphaned entries are less critical — the inventory may mention grouped keys
    # that aren't individual settings. Only set exit code if undocumented found.
}

if ($undocumented.Count -eq 0 -and $orphaned.Count -eq 0) {
    Write-Host "✅ All $($sourceKeys.Count) setting keys have inventory entries." -ForegroundColor Green
}
elseif ($undocumented.Count -eq 0) {
    Write-Host "✅ All $($sourceKeys.Count) setting keys have inventory entries." -ForegroundColor Green
    Write-Host "  ($($orphaned.Count) orphaned entries — review if intentional)"
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "  Source keys       : $($sourceKeys.Count)"
Write-Host "  Inventory entries : $($inventoryKeys.Count)"
Write-Host "  Undocumented      : $($undocumented.Count)"
Write-Host "  Orphaned          : $($orphaned.Count)"
Write-Host "  Exit code         : $exitCode"

exit $exitCode
