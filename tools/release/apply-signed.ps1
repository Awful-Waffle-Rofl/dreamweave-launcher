# Copies the files SignPath returned over the originals in -TargetDir and proves each one was really replaced.
# For every -Name: exactly one file of that name must exist under -SignedDir, and its SHA-256 must DIFFER from the
# file in -TargetDir (an identical file means SignPath returned the input unsigned). Throws on any miss.
param(
    [Parameter(Mandatory = $true)][string]$SignedDir,
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [Parameter(Mandatory = $true)][string[]]$Name
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SignedDir -PathType Container)) { throw "Signed output folder not found: $SignedDir" }
if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) { throw "Target folder not found: $TargetDir" }

foreach ($n in $Name) {
    $target = Join-Path $TargetDir $n
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Target file not found: $target" }
    $hits = @(Get-ChildItem -LiteralPath $SignedDir -Recurse -File -Filter $n)
    if ($hits.Count -ne 1) { throw "Expected exactly one '$n' under $SignedDir, found $($hits.Count)" }
    $before = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $after = (Get-FileHash -LiteralPath $hits[0].FullName -Algorithm SHA256).Hash
    if ($before -eq $after) { throw "'$n' came back from SignPath byte-identical to the unsigned file; it was not signed" }
    Copy-Item -LiteralPath $hits[0].FullName -Destination $target -Force
    Write-Host "Replaced $target with the signed file ($before -> $after)"
}
