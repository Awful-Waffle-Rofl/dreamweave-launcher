# Checks the built launcher exe: size limit, and that nothing but the allowed files sits next to it.
# On success, exports EXE_PATH to $env:GITHUB_ENV when running in GitHub Actions.
# Mirrors the inline checks in .github/workflows/build.yml.
param(
    [string]$SearchRoot = 'src/Launcher/bin/Release',
    [long]$MaxBytes = 3145728
)

$ErrorActionPreference = 'Stop'

$exe = Get-ChildItem -Path $SearchRoot -Recurse -Filter DreamWeaveLauncher.exe | Select-Object -First 1
if (-not $exe) { Write-Error 'DreamWeaveLauncher.exe not found'; exit 1 }
Write-Host "$($exe.FullName) = $($exe.Length) bytes"
if ($exe.Length -gt $MaxBytes) { Write-Error "Exe exceeds size limit ($MaxBytes bytes)"; exit 1 }

$dir = Split-Path $exe.FullName
$allowed = @('DreamWeaveLauncher.exe', 'DreamWeaveLauncher.exe.config', 'DreamWeaveLauncher.pdb')
$extra = Get-ChildItem $dir -File | Where-Object { $allowed -notcontains $_.Name }
if ($extra) { Write-Error "Unexpected files next to the exe: $($extra.Name -join ', ')"; exit 1 }

if ($env:GITHUB_ENV) {
    "EXE_PATH=$($exe.FullName)" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
}
Write-Host 'Exe size and layout OK.'
