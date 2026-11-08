# Prints the manifest "launcher" block for a release, to paste into manifest.json BEFORE signing.
# Usage: ./tools/release/print-manifest-launcher-block.ps1 -Version 1.0.0 [-DownloadPage <https url>]
# Prints only "version" (and "downloadPage" when given). The launcher no longer downloads itself, so the
# old url, sha256 and size members are not printed; keep your existing minVersion and downloadPage lines in
# manifest.json as they are, and delete any leftover url, sha256, size or authenticodeSubject lines.
# Optional -OutFile writes the same text as UTF-8 without BOM.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$DownloadPage,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$ver = $Version.TrimStart('v')
if ($ver -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version '$Version' is not MAJOR.MINOR.PATCH"
    exit 1
}
if ($DownloadPage -and ($DownloadPage -notmatch '^https://[^\s"]+$')) {
    Write-Error "DownloadPage '$DownloadPage' is not an https URL without spaces or quotes"
    exit 1
}

$lines = @('"launcher": {')
if ($DownloadPage) {
    $lines += ('  "version": "' + $ver + '",')
    $lines += ('  "downloadPage": "' + $DownloadPage + '"')
} else {
    $lines += ('  "version": "' + $ver + '"')
}
$lines += '}'
$text = $lines -join "`n"

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