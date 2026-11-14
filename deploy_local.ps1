<#
.SYNOPSIS
    Builds and deploys AITraffic mod directly to the local Derail Valley Mods directory.
#>
param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild = $false,
    [string]$RoslynVersion = "4.14.0",
    [string]$CscToolPath = $null,
    [switch]$NoRoslynDownload = $false
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

. (Join-Path $scriptDir "Resolve-RoslynToolset.ps1")

# 0. Pre-Build Static Gatekeeper: Verify Architecture Invariants & Anti-Patterns
$verifierScript = Join-Path $scriptDir "verify_invariants.py"
if (Test-Path $verifierScript) {
    Write-Host "[Gatekeeper] Running static invariant verification (verify_invariants.py)..." -ForegroundColor Cyan
    & py "$verifierScript"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Static invariant verification failed with exit code $LASTEXITCODE. Aborting deployment."
        exit $LASTEXITCODE
    }
    Write-Host "[Gatekeeper] Invariant verification passed.`n" -ForegroundColor Green
} else {
    Write-Error "Static gatekeeper script not found at $verifierScript"
    exit 1
}

# Auto-detect Derail Valley install path
$dvBase = "C:\Program Files (x86)\Steam\steamapps\common\Derail Valley"
if (-not (Test-Path $dvBase)) {
    $dvBase = "J:\SteamLibrary\steamapps\common\Derail Valley"
}
$modsFolder = Join-Path $dvBase "Mods"
if (-not (Test-Path $modsFolder) -and (Test-Path (Join-Path $dvBase "mods"))) {
    $modsFolder = Join-Path $dvBase "mods"
}
$destDir = Join-Path $modsFolder "AITraffic"

if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

$binDir = Join-Path $scriptDir "bin\$Configuration"
$dllPath = Join-Path $binDir "AITraffic.dll"
$pdbPath = Join-Path $binDir "AITraffic.pdb"

if (-not $NoBuild) {
    Write-Host "[Deploy] Building AITraffic in $Configuration configuration..." -ForegroundColor Yellow
    $csprojPath = Join-Path $scriptDir "AITraffic.csproj"
    Invoke-AITrafficBuild `
        -ScriptDir $scriptDir `
        -ProjectPath $csprojPath `
        -Configuration $Configuration `
        -DVInstallPath $dvBase `
        -ExpectedDllPath $dllPath `
        -RoslynVersion $RoslynVersion `
        -CscToolPath $CscToolPath `
        -NoRoslynDownload:$NoRoslynDownload
}

if (-not (Test-Path $dllPath)) {
    Write-Error "Compiled assembly not found at $dllPath"
}

Copy-Item $dllPath -Destination $destDir -Force
if (Test-Path $pdbPath) {
    Copy-Item $pdbPath -Destination $destDir -Force
}

$infoJsonPath = Join-Path $scriptDir "Info.json"
if (Test-Path $infoJsonPath) {
    Copy-Item $infoJsonPath -Destination $destDir -Force
}

$iniPath = Join-Path $scriptDir "settings.ini"
$destIniPath = Join-Path $destDir "settings.ini"
if ((Test-Path $iniPath) -and (-not (Test-Path $destIniPath))) {
    Copy-Item $iniPath -Destination $destDir
    Write-Host "[Deploy] Deployed default settings.ini" -ForegroundColor Cyan
}

$deployedItem = Get-Item (Join-Path $destDir "AITraffic.dll")
Write-Host "[Deploy] Successfully deployed to $destDir" -ForegroundColor Green
Write-Host "[Deploy] DLL LastWriteTime: $($deployedItem.LastWriteTime)" -ForegroundColor Cyan
