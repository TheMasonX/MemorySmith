$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repoRoot
try {
    dotnet test .\MemorySmith.Tests\MemorySmith.Tests.csproj --filter "FullyQualifiedName~LiveMemoryRecordValidationTests" --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'Memory record validation failed.'
    }
}
finally {
    Pop-Location
}