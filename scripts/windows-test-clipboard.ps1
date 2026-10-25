# Clipboard access is exclusive across the desktop. Retry only native clipboard
# contention, for at most five seconds; assertions and other errors still fail.
function Invoke-TestClipboard([scriptblock] $Operation) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try { return (& $Operation) }
        catch [System.Runtime.InteropServices.ExternalException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
        }
        catch [System.Management.Automation.MethodInvocationException] {
            if ($_.Exception.InnerException -isnot [System.Runtime.InteropServices.ExternalException] -or [DateTime]::UtcNow -ge $deadline) { throw }
        }
        if ('System.Windows.Forms.Application' -as [type]) { [System.Windows.Forms.Application]::DoEvents() }
        Start-Sleep -Milliseconds 50
    } while ($true)
}
