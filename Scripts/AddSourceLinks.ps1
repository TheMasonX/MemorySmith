param([string]$Root = (Split-Path $PSScriptRoot -Parent))

Set-Location $Root

$updates = @{
    "project-wiki-data-folder-policy"     = @(
        @{ Label="Data/Memories/"; Uri="%MemorySmithRepo%Data/Memories/" }
        @{ Label="appsettings.json"; Uri="%MemorySmithRepo%MemorySmith.App/appsettings.json" }
    )
    "project-wiki-storage-rules"          = @(
        @{ Label="FileMemoryStore.cs"; Uri="%MemorySmithRepo%MemorySmith.Storage/FileMemoryStore.cs" }
        @{ Label="IMemoryStore.cs"; Uri="%MemorySmithRepo%MemorySmith.Storage/IMemoryStore.cs" }
    )
    "project-wiki-validation-command"     = @(
        @{ Label="MemorySmith.slnx"; Uri="%MemorySmithRepo%MemorySmith.slnx" }
        @{ Label="MemorySmith.Tests.csproj"; Uri="%MemorySmithRepo%MemorySmith.Tests/MemorySmith.Tests.csproj" }
    )
    "project-wiki-mcp-context-pack"       = @(
        @{ Label="McpController.cs"; Uri="%MemorySmithRepo%MemorySmith.App/Controllers/McpController.cs"; StartLine=1; EndLine=50 }
        @{ Label="MemoryApplicationService.cs (BuildContextPackAsync)"; Uri="%MemorySmithRepo%MemorySmith.App/Services/MemoryApplicationService.cs"; StartLine=193; EndLine=260 }
    )
    "project-wiki-mcp-integration"        = @(
        @{ Label="McpController.cs"; Uri="%MemorySmithRepo%MemorySmith.App/Controllers/McpController.cs" }
        @{ Label="Program.cs (MCP registration)"; Uri="%MemorySmithRepo%MemorySmith.App/Program.cs"; StartLine=1; EndLine=80 }
    )
    "project-wiki-semantic-ui-current"    = @(
        @{ Label="MemoryViewer.razor"; Uri="%MemorySmithRepo%MemorySmith.App/Components/Pages/MemoryViewer.razor" }
        @{ Label="NavMenu.razor"; Uri="%MemorySmithRepo%MemorySmith.App/Components/Layout/NavMenu.razor" }
    )
    "project-wiki-scope-boundaries"       = @(
        @{ Label="Program.cs"; Uri="%MemorySmithRepo%MemorySmith.App/Program.cs" }
        @{ Label="MemorySmith.App.csproj"; Uri="%MemorySmithRepo%MemorySmith.App/MemorySmith.App.csproj" }
    )
    "project-wiki-search-roadmap"         = @(
        @{ Label="MemoryApplicationService.cs"; Uri="%MemorySmithRepo%MemorySmith.App/Services/MemoryApplicationService.cs" }
        @{ Label="MemoryQueries.cs"; Uri="%MemorySmithRepo%MemorySmith.App/Services/MemoryQueries.cs" }
    )
    "project-wiki-semantic-search-gap"    = @(
        @{ Label="MemoryApplicationService.cs (SemanticSearchAsync)"; Uri="%MemorySmithRepo%MemorySmith.App/Services/MemoryApplicationService.cs"; StartLine=120; EndLine=145 }
        @{ Label="ScoringTests.cs"; Uri="%MemorySmithRepo%MemorySmith.Tests/ScoringTests.cs" }
    )
    "project-wiki-windows-service-operations" = @(
        @{ Label="Program.cs"; Uri="%MemorySmithRepo%MemorySmith.App/Program.cs" }
    )
    "project-wiki-generalization-friction" = @(
        @{ Label="appsettings.Development.json"; Uri="%MemorySmithRepo%MemorySmith.App/appsettings.Development.json" }
    )
}

foreach ($id in $updates.Keys) {
    $path = Join-Path $Root "Data\Memories\Core\$id.json"
    if (-not (Test-Path $path)) { Write-Warning "Missing: $path"; continue }
    
    $j = Get-Content $path -Raw | ConvertFrom-Json
    
    $links = $updates[$id] | ForEach-Object {
        $ht = $_
        $sl = [ordered]@{ Label=$ht.Label; Uri=$ht.Uri }
        if ($ht.ContainsKey("StartLine")) { $sl.StartLine = $ht.StartLine }
        if ($ht.ContainsKey("EndLine"))   { $sl.EndLine   = $ht.EndLine   }
        [PSCustomObject]$sl
    }
    
    if ($j.PSObject.Properties['SourceLinks']) {
        $j.SourceLinks = $links
    } else {
        $j | Add-Member -NotePropertyName SourceLinks -NotePropertyValue $links
    }
    $j | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding UTF8NoBOM
    Write-Host "Updated: $id"
}

Write-Host ""
Write-Host "=== Verification ==="
Get-ChildItem "$Root\Data\Memories\Core\project-wiki-*.json" | ForEach-Object {
    $j2 = Get-Content $_.FullName | ConvertFrom-Json
    "$($j2.Id): src=$($j2.SourceLinks.Count)"
}
