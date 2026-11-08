# Builds the per-user Inno Setup installer: <OutDir>\DreamWeaveLauncher-Setup-<version>.exe.
# The version is read from <Version> in Directory.Build.props, the same way assert-version.ps1 does.
# Needs Inno Setup 6 (preinstalled on GitHub's Windows runners). Fails clearly when it is missing;
# it never installs anything.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$PropsPath = 'Directory.Build.props',
    [string]$ScriptPath = 'installer/DreamWeaveLauncher.iss'
)

$ErrorActionPreference = 'Stop'

# --- version (same source and rule as assert-version.ps1) ---
[xml]$props = Get-Content -LiteralPath $PropsPath -Raw
$node = $props.SelectSingleNode('//Version')
if (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
    Write-Error "No <Version> in $PropsPath"
    exit 1
}
$version = $node.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "<Version> '$version' in $PropsPath is not MAJOR.MINOR.PATCH"
    exit 1
}

# --- the exe and its .config ---
if (-not (Test-Path -LiteralPath $ExePath)) { Write-Error "Exe not found: $ExePath"; exit 1 }
$exeAbs = (Resolve-Path -LiteralPath $ExePath).Path
$exeDir = Split-Path -Parent $exeAbs
if (-not (Test-Path -LiteralPath (Join-Path $exeDir 'DreamWeaveLauncher.exe.config'))) {
    Write-Error "DreamWeaveLauncher.exe.config not found next to $exeAbs"
    exit 1
}

# --- ISCC.exe ---
$candidates = @()
if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }
$iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}
if (-not $iscc) {
    Write-Error ("Inno Setup 6 (ISCC.exe) was not found. Looked in: " + ($candidates -join '; ') + " and on PATH. Install Inno Setup 6 (https://jrsoftware.org/isdl.php); GitHub's Windows runners already have it.")
    exit 1
}

# ISCC.exe carries no file version resource (reads 0.0.0.0); its startup banner states the version:
# "Inno Setup 6.x.y Command-Line Compiler". Run with no arguments it prints the banner and usage.
$banner = (& $iscc 2>&1 | Out-String)
$innoVersion = $null
if ($banner -match 'Inno Setup\s+(\d+\.\d+(\.\d+)*)') { $innoVersion = $Matches[1] }
if (-not $innoVersion) {
    Write-Error "Could not read the Inno Setup version from the ISCC banner: $banner"
    exit 1
}
Write-Host "Inno Setup compiler: $iscc (version $innoVersion)"
if ($innoVersion -notmatch '^6\.') {
    Write-Error "Inno Setup major version 6 is required, found '$innoVersion'"
    exit 1
}

# --- compile ---
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outAbs = (Resolve-Path -LiteralPath $OutDir).Path
$scriptAbs = (Resolve-Path -LiteralPath $ScriptPath).Path

& $iscc "/Qp" "/DAppVersion=$version" "/DExeDir=$exeDir" "/O$outAbs" $scriptAbs
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC failed with exit code $LASTEXITCODE"; exit 1 }

$setup = Join-Path $outAbs "DreamWeaveLauncher-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setup)) { Write-Error "Expected output not found: $setup"; exit 1 }
$setupInfo = Get-Item -LiteralPath $setup
Write-Host "Built $($setupInfo.FullName) ($($setupInfo.Length) bytes), product version $($setupInfo.VersionInfo.ProductVersion)"
