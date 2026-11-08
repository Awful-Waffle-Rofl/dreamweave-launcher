# Copies the setup installer into OutDir and writes SHA256SUMS next to it in the standard
# "<lowercase hash>  <file>" format (two spaces, LF line endings, UTF-8 without BOM).
# The bare launcher exe is no longer a release asset: the setup installs it.
param(
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SetupPath)) { Write-Error "Setup not found: $SetupPath"; exit 1 }
$name = Split-Path -Leaf $SetupPath
if ($name -notmatch '^DreamWeaveLauncher-Setup-\d+\.\d+\.\d+\.exe$') {
    Write-Error "Unexpected setup file name '$name'"
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outAbs = (Resolve-Path -LiteralPath $OutDir).Path
$dest = Join-Path $outAbs $name
Copy-Item -LiteralPath $SetupPath -Destination $dest -Force

$hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
$line = $hash + '  ' + $name + "`n"
[System.IO.File]::WriteAllText((Join-Path $outAbs 'SHA256SUMS'), $line, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "SHA256SUMS: $hash  $name"
