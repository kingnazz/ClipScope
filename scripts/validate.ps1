$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore ClipScope.sln --locked-mode -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build ClipScope.sln -c Release -p:Platform=x64 --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test --project tests/ClipScope.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    dotnet test --project tests/ClipScope.Infrastructure.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Storage tests failed' }
} finally { Pop-Location }
