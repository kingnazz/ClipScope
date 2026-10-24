$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
Assert-IsolatedDesktopRunner
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System.Runtime.InteropServices;
public static class RcEnvironmentNative { [DllImport("user32.dll")] public static extern uint GetDpiForSystem(); }
'@
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$record = [ordered]@{ os = $os.Caption; version = $os.Version; build = $os.BuildNumber; architecture = $os.OSArchitecture;
    cpu = $cpu.Name; cores = $cpu.NumberOfCores; memoryGiB = [Math]::Round($os.TotalVisibleMemorySize / 1048576,1);
    host = 'ephemeral GitHub-hosted VM'; dpi = [RcEnvironmentNative]::GetDpiForSystem();
    screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { @{ width = $_.Bounds.Width; height = $_.Bounds.Height; primary = $_.Primary } });
    sha = (& git rev-parse HEAD); physicalWindows11 = 'NOT TESTED' }
$evidence = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/rc/evidence'
[void][IO.Directory]::CreateDirectory($evidence)
$record | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $evidence 'environment.json') -Encoding UTF8
Write-Output ('::notice title=RC environment::' + ($record | ConvertTo-Json -Depth 4 -Compress))
