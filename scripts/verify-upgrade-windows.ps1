# Verify the real retained main-profile data through the installed UI.
param([int] $TargetProcessId, [string] $OwnershipReceipt)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-rc-common.ps1')
$data = Join-Path $env:LOCALAPPDATA 'ClipScope'
$owner = Get-Content -LiteralPath $OwnershipReceipt -Raw | ConvertFrom-Json
if ($owner.Path -ne $data) { throw 'Upgrade UI profile handoff does not match this user.' }
Assert-NoTestReparsePoint $data
$marker = Join-Path $data '.clipscope-test-owner'
Assert-NoTestReparsePoint $marker
if ([IO.File]::ReadAllText($marker) -cne $owner.Token) { throw 'Upgrade UI profile ownership changed.' }
$app = [Diagnostics.Process]::GetProcessById($TargetProcessId)
try {
    Open-RcHistory $app 'hotkey'
    (Get-RcPattern 'Search clipboard history' ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('pinned sentinel')
    Wait-Rc { $null -ne (Get-RcPattern 'Unpin' ([System.Windows.Automation.InvokePattern]::Pattern)) } 'retained main pin and FTS result'
    Wait-Rc {
        $images = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Image)))
        foreach ($image in $images) {
            if ($image.Current.Name -like 'Snapshot of *ClipScope CI Source*' -and $image.Current.ItemStatus -eq 'Preview ready') { return $true }
        }
        return $false
    } 'retained main context PNG decoded in installed UI'
    [System.Windows.Forms.Clipboard]::Clear()
    Invoke-RcAction 'Copy'
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq 'ClipScope clear pinned sentinel' } 'retained main clip restored exactly'
    Write-Output '::notice title=RC upgrade UI::PASS: retained main pin, FTS result, context PNG decode and exact clipboard restoration through installed RC'
} catch {
    Write-Output ('::error title=RC upgrade UI::' + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A') + " in $script:phase")
    throw
} finally { [System.Windows.Forms.Clipboard]::Clear(); $app.Dispose() }
