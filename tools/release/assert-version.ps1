# Asserts that a release tag (vMAJOR.MINOR.PATCH) equals the <Version> in Directory.Build.props
# and, when -ExePath is given, the built exe's assembly version (MAJOR.MINOR.PATCH.0), and, when
# -SetupPath is given, the setup's product version (MAJOR.MINOR.PATCH).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$ExePath,
    [string]$SetupPath,
    [string]$PropsPath = 'Directory.Build.props'
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)$') {
    Write-Error "Tag '$Tag' is not of the form vMAJOR.MINOR.PATCH"
    exit 1
}
$tagVersion = $Tag.Substring(1)

[xml]$props = Get-Content -LiteralPath $PropsPath -Raw
$node = $props.SelectSingleNode('//Version')
if (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
    Write-Error "No <Version> in $PropsPath"
    exit 1
}
$propsVersion = $node.InnerText.Trim()
if ($propsVersion -ne $tagVersion) {
    Write-Error "Tag $Tag does not match <Version> $propsVersion in $PropsPath"
    exit 1
}

if ($ExePath) {
    $asm = [System.Reflection.AssemblyName]::GetAssemblyName($ExePath).Version.ToString()
    if ($asm -ne "$tagVersion.0") {
        Write-Error "Exe assembly version $asm does not match expected $tagVersion.0"
        exit 1
    }
}

if ($SetupPath) {
    # Inno pads the product version with trailing spaces.
    $setupVersion = ([string](Get-Item -LiteralPath $SetupPath).VersionInfo.ProductVersion).Trim()
    if ($setupVersion -ne $tagVersion) {
        Write-Error "Setup product version '$setupVersion' does not match expected $tagVersion"
        exit 1
    }
}

Write-Host "Tag $Tag matches version $propsVersion."
