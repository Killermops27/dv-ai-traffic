<#
.SYNOPSIS
    Builds and deploys AITraffic mod directly to the local Derail Valley Mods directory.
#>
param(
    [string]$Configuration = "Debug",
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
    Write-Host "[Deploy] Building AITraffic in $Configuration configuration..." -ForegroundColor Yellow
    $msbuildPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    if (-not (Test-Path $msbuildPath)) {
        $msbuildPath = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    }

    $csprojPath = Join-Path $scriptDir "AITraffic.csproj"
    & $msbuildPath "$csprojPath" /p:Configuration=$Configuration /t:Build /verbosity:minimal /nologo

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
    }
}

$binDir = Join-Path $scriptDir "bin\$Configuration"
$dllPath = Join-Path $binDir "AITraffic.dll"
$pdbPath = Join-Path $binDir "AITraffic.pdb"

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
