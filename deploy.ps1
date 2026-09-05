# Deploy script for Derail Valley AI Traffic mod
$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

$dvPath = "J:\SteamLibrary\steamapps\common\Derail Valley"
$destDir = Join-Path $dvPath "Mods\AITraffic"

if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

$binRelease = Join-Path $scriptDir "bin\Release"
Copy-Item (Join-Path $binRelease "AITraffic.dll") -Destination $destDir -Force
Copy-Item (Join-Path $binRelease "AITraffic.pdb") -Destination $destDir -Force
Copy-Item (Join-Path $scriptDir "Info.json") -Destination $destDir -Force

# Clean old cache files
Get-ChildItem $destDir -Filter "*.cache*" | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Successfully deployed AI Traffic to $destDir" -ForegroundColor Green
Get-ChildItem $destDir | Select-Object Name, Length, LastWriteTime
