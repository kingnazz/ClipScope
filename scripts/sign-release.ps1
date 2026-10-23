# Optional trusted release-machine hook. No certificate or secret is shipped.
param([Parameter(Mandatory)][string] $Path)
$ErrorActionPreference = 'Stop'
if ($env:CLIPSCOPE_SIGNING_THUMBPRINT -notmatch '^[0-9a-fA-F]{40}$') { throw 'Select an installed CurrentUser code-signing certificate with CLIPSCOPE_SIGNING_THUMBPRINT.' }
if ($env:CLIPSCOPE_TIMESTAMP_URL -notmatch '^https://') { throw 'Set CLIPSCOPE_TIMESTAMP_URL to your RFC 3161 timestamp service over HTTPS.' }
$signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
& $signtool sign /sha1 $env:CLIPSCOPE_SIGNING_THUMBPRINT /s My /fd SHA256 /tr $env:CLIPSCOPE_TIMESTAMP_URL /td SHA256 $Path
if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing or timestamping failed.' }
& $signtool verify /pa /all $Path
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) { throw 'A trusted signature and timestamp are required.' }
