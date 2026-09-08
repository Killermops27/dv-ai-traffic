<#
.SYNOPSIS
    Builds and deploys AITraffic mod directly to the local Derail Valley Mods directory.
#>
param(
    [switch]$NoBuild = $false
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

$destDir = "J:\SteamLibrary\steamapps\common\Derail Valley\Mods\AITraffic"
if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

if (-not $NoBuild) {
    Write-Host "[Deploy] Building AITraffic in Release configuration..." -ForegroundColor Yellow
    $msbuildPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    if (-not (Test-Path $msbuildPath)) {
        $msbuildPath = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    }

    $csprojPath = Join-Path $scriptDir "AITraffic.csproj"
    & $msbuildPath "$csprojPath" /p:Configuration=Release /t:Build /verbosity:minimal /nologo

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
    }
}

$binReleaseDir = Join-Path $scriptDir "bin\Release"
$dllPath = Join-Path $binReleaseDir "AITraffic.dll"
$pdbPath = Join-Path $binReleaseDir "AITraffic.pdb"

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

$deployedItem = Get-Item (Join-Path $destDir "AITraffic.dll")
Write-Host "[Deploy] Successfully deployed to $destDir" -ForegroundColor Green
Write-Host "[Deploy] DLL LastWriteTime: $($deployedItem.LastWriteTime)" -ForegroundColor Cyan
