param(
    [switch]$IncludeCoverage,
    [switch]$IncludeE2E,
    [switch]$IncludeDocs,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host ("==> " + $Name) -ForegroundColor Cyan
    & $Action
    Write-Host ("OK: " + $Name) -ForegroundColor Green
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        Invoke-Step -Name "Build solution" -Action {
            dotnet build MemorySmith.slnx -v minimal
        }
    }

    if (-not $SkipTests) {
        Invoke-Step -Name "Run test suite" -Action {
            dotnet test MemorySmith.slnx -v minimal
        }
    }

    Invoke-Step -Name "Validate task records" -Action {
        & (Join-Path $repoRoot "Scripts/Test-TaskRecords.ps1")
    }

    Invoke-Step -Name "Validate markdown page links" -Action {
        & (Join-Path $repoRoot "Scripts/Test-PageLinks.ps1")
    }

    Invoke-Step -Name "Validate markdown path literals" -Action {
        & (Join-Path $repoRoot "Scripts/Test-PagePathLiterals.ps1")
    }

    if ($IncludeCoverage) {
        Invoke-Step -Name "Collect Cobertura coverage" -Action {
            dotnet test MemorySmith.slnx --configuration Release --collect:"XPlat Code Coverage" --results-directory artifacts/TestResults -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
        }
    }

    if ($IncludeE2E) {
        Push-Location (Join-Path $repoRoot "e2e")
        try {
            Invoke-Step -Name "Install e2e dependencies" -Action {
                npm ci
            }

            Invoke-Step -Name "Install Playwright Chromium" -Action {
                npx playwright install chromium
            }

            Invoke-Step -Name "Run route-hop browser regression" -Action {
                npm run test:nav-freeze
            }
        }
        finally {
            Pop-Location
        }
    }

    if ($IncludeDocs) {
        Invoke-Step -Name "Generate Doxygen wiki" -Action {
            doxygen docs/Doxyfile
        }

        Invoke-Step -Name "Rebuild static wiki site" -Action {
            & (Join-Path $repoRoot "Scripts/Publish-WikiSite.ps1")
        }
    }

    Write-Host "Validation complete." -ForegroundColor Green
}
finally {
    Pop-Location
}