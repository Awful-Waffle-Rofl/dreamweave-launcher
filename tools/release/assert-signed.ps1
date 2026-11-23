# Verifies the Authenticode signatures of the release binaries and the setup.
# -Enabled 'true'  : every -Path file must have a Valid signature AND a timestamp, otherwise this throws.
# -Enabled anything else : prints a loud UNSIGNED warning (GitHub annotation) and passes, so an unconfigured
#                          Artifact Signing setup still produces the unsigned releases the project ships today.
param(
    [Parameter(Mandatory = $true)][string]$Enabled,
    [Parameter(Mandatory = $true)][string[]]$Path
)

$ErrorActionPreference = 'Stop'

if ($Enabled -ne 'true') {
    Write-Host '::warning::Release is UNSIGNED (Artifact Signing not configured)'
    exit 0
}

$bad = New-Object System.Collections.Generic.List[string]
foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { $bad.Add("${p}: file not found"); continue }
    $sig = Get-AuthenticodeSignature -LiteralPath $p
    if ($sig.Status -ne 'Valid') { $bad.Add("${p}: signature status is $($sig.Status) ($($sig.StatusMessage))"); continue }
    if ($null -eq $sig.TimeStamperCertificate) { $bad.Add("${p}: signed but carries no timestamp"); continue }
    Write-Host ("{0}: Valid, signer '{1}', timestamped" -f $p, $sig.SignerCertificate.Subject)
}
if ($bad.Count -gt 0) { throw ("Signature check failed:`n" + ($bad -join "`n")) }
Write-Host "All $($Path.Count) files are validly signed and timestamped."
