# Prints the manifest "launcher" block for a built exe, to paste into manifest.json BEFORE signing.
# Usage: ./tools/release/print-manifest-launcher-block.ps1 -ExePath <DreamWeaveLauncher.exe> -Version 1.0.0
# Optional -OutFile writes the same text as UTF-8 without BOM.
# The block has the four release-derived fields only; keep your existing minVersion and
# authenticodeSubject lines in manifest.json as they are.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$ver = $Version.TrimStart('v')
if ($ver -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version '$Version' is not MAJOR.MINOR.PATCH"
    exit 1
}
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Error "Exe not found: $ExePath"
    exit 1
}

$item = Get-Item -LiteralPath $ExePath
$sha = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
$url = "https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/download/v$ver/DreamWeaveLauncher.exe"

$text = @(
    '"launcher": {',
    ('  "version": "' + $ver + '",'),
    ('  "url": "' + $url + '",'),
    ('  "sha256": "' + $sha + '",'),
    ('  "size": ' + $item.Length),
    '}'
) -join "`n"

Write-Host $text
if ($OutFile) {
    if ([System.IO.Path]::IsPathRooted($OutFile)) {
        $full = [System.IO.Path]::GetFullPath($OutFile)
    } else {
        $full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutFile))
    }
    [System.IO.File]::WriteAllText($full, $text + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $full"
}
