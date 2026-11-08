# Checks the built launcher output against installer/shipped-files.txt (the single source of truth):
# every "ship" file is present, nothing else but the "build-only" files sits next to the exe, the exe is
# under its size cap and the shipped files together are under the total cap.
# On success, exports EXE_PATH to $env:GITHUB_ENV when running in GitHub Actions.
# With -StageDir, also copies exactly the "ship" files there (the CI artifact of the loose build).
param(
    [string]$SearchRoot = 'src/Launcher/bin/Release',
    [string]$ListPath = 'installer/shipped-files.txt',
    [string]$StageDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ListPath)) { Write-Error "Shipped-files list not found: $ListPath"; exit 1 }
$ship = New-Object System.Collections.Generic.List[string]
$buildOnly = New-Object System.Collections.Generic.List[string]
$maxExe = $null
$maxTotal = $null
foreach ($raw in (Get-Content -LiteralPath $ListPath)) {
    $line = $raw.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
    $parts = $line -split '\s+'
    if ($parts.Count -ne 2) { Write-Error "Bad line in ${ListPath}: '$line'"; exit 1 }
    switch ($parts[0]) {
        'ship' { $ship.Add($parts[1]) }
        'build-only' { $buildOnly.Add($parts[1]) }
        'max-exe-bytes' { $maxExe = [long]$parts[1] }
        'max-total-bytes' { $maxTotal = [long]$parts[1] }
        default { Write-Error "Unknown keyword in ${ListPath}: '$line'"; exit 1 }
    }
}
if ($ship.Count -eq 0 -or $null -eq $maxExe -or $null -eq $maxTotal) { Write-Error "$ListPath must list ship files and both size caps"; exit 1 }

$exe = Get-ChildItem -Path $SearchRoot -Recurse -Filter DreamWeaveLauncher.exe | Select-Object -First 1
if (-not $exe) { Write-Error 'DreamWeaveLauncher.exe not found'; exit 1 }
$dir = Split-Path $exe.FullName

$total = 0L
foreach ($name in $ship) {
    $f = Join-Path $dir $name
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Write-Error "Shipped file missing next to the exe: $name"; exit 1 }
    $len = (Get-Item -LiteralPath $f).Length
    Write-Host ("{0} = {1} bytes" -f $f, $len)
    $total += $len
}
Write-Host "Shipped total = $total bytes (cap $maxTotal)"
if ($exe.Length -gt $maxExe) { Write-Error "Exe exceeds size limit ($maxExe bytes)"; exit 1 }
if ($total -gt $maxTotal) { Write-Error "Shipped files exceed the total size limit ($maxTotal bytes)"; exit 1 }

$allowed = @($ship) + @($buildOnly)
$extra = Get-ChildItem $dir -Force | Where-Object { $allowed -notcontains $_.Name }
if ($extra) { Write-Error "Unexpected files or folders next to the exe: $($extra.Name -join ', ')"; exit 1 }

if ($StageDir) {
    New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
    foreach ($name in $ship) { Copy-Item -LiteralPath (Join-Path $dir $name) -Destination $StageDir -Force }
    Write-Host "Staged $($ship.Count) shipped files in $StageDir"
}

if ($env:GITHUB_ENV) {
    "EXE_PATH=$($exe.FullName)" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
}
Write-Host 'Exe size, total size and layout OK.'