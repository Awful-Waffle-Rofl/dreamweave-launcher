# Copies the built exe into OutDir and writes SHA256SUMS next to it in the standard
# "<lowercase hash>  <file>" format (two spaces, LF line endings, UTF-8 without BOM).
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outAbs = (Resolve-Path -LiteralPath $OutDir).Path
$dest = Join-Path $outAbs 'DreamWeaveLauncher.exe'
Copy-Item -LiteralPath $ExePath -Destination $dest -Force

$hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
$line = $hash + '  DreamWeaveLauncher.exe' + "`n"
[System.IO.File]::WriteAllText((Join-Path $outAbs 'SHA256SUMS'), $line, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "SHA256SUMS: $hash  DreamWeaveLauncher.exe"
