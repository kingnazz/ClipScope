$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    function Invoke-CheckedDotnet([string[]] $Arguments) {
        $lines = @(& dotnet @Arguments 2>&1 | Tee-Object -Variable output)
        $lines | ForEach-Object { Write-Output $_ }
        if ($LASTEXITCODE -ne 0) {
            $detail = ($lines | Select-Object -Last 15) -join "`n"
            Write-Output ('::error title=Windows validation::' + $detail.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A'))
            throw "dotnet $($Arguments[0]) failed."
        }
    }
    Invoke-CheckedDotnet -Arguments @('restore','ClipScope.sln','--locked-mode','-p:Platform=x64')
    Invoke-CheckedDotnet -Arguments @('build','ClipScope.sln','-c','Release','-p:Platform=x64','--no-restore','-warnaserror')
    dotnet test --project tests/ClipScope.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    dotnet test --project tests/ClipScope.Infrastructure.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Storage tests failed' }
} finally { Pop-Location }
