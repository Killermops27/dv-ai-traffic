<#
.SYNOPSIS
    Builds and packages the Derail Valley AI Traffic mod into a UMM-ready release ZIP.

.PARAMETER NoBuild
    Skip the MSBuild step and package from existing bin/Release artifacts.

.EXAMPLE
    .\package_release.ps1
    .\package_release.ps1 -NoBuild
#>

param(
    [switch]$NoBuild = $false
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

$infoJsonPath = Join-Path $scriptDir "Info.json"
if (-not (Test-Path $infoJsonPath)) {
    Write-Error "Info.json not found at $infoJsonPath"
}

$info = Get-Content $infoJsonPath -Raw | ConvertFrom-Json
$modId = $info.Id
$version = $info.Version

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Packaging $modId v$version for Release " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Build project in Release mode (unless -NoBuild is specified)
if (-not $NoBuild) {
    Write-Host "[1/3] Building $modId in Release configuration..." -ForegroundColor Yellow
    
    # Locate MSBuild
    $msbuildPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    if (-not (Test-Path $msbuildPath)) {
        $msbuildPath = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    }

    if (-not (Test-Path $msbuildPath)) {
        Write-Error "Could not locate MSBuild.exe on this system."
    }

    $csprojPath = Join-Path $scriptDir "$modId.csproj"
    & $msbuildPath "$csprojPath" /p:Configuration=Release /t:Rebuild /verbosity:minimal /nologo

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
    }
    Write-Host "Build completed successfully." -ForegroundColor Green
} else {
    Write-Host "[1/3] Skipping build step (-NoBuild specified)..." -ForegroundColor DarkGray
}

# 2. Stage release files
Write-Host "[2/3] Staging mod release files..." -ForegroundColor Yellow
$distDir = Join-Path $scriptDir "dist"
$stagingDir = Join-Path $distDir "staging"
$modStagingDir = Join-Path $stagingDir $modId

if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $modStagingDir -Force | Out-Null

# Copy Info.json
Copy-Item (Join-Path $scriptDir "Info.json") -Destination $modStagingDir -Force

# Copy DLL and PDB from bin/Release
$binReleaseDir = Join-Path $scriptDir "bin\Release"
$dllPath = Join-Path $binReleaseDir "$modId.dll"
$pdbPath = Join-Path $binReleaseDir "$modId.pdb"

if (-not (Test-Path $dllPath)) {
    Write-Error "Compiled assembly not found at $dllPath. Run without -NoBuild or compile in Release mode first."
}

Copy-Item $dllPath -Destination $modStagingDir -Force
if (Test-Path $pdbPath) {
    Copy-Item $pdbPath -Destination $modStagingDir -Force
}

# 3. Create ZIP archive
Write-Host "[3/3] Creating UMM-compliant ZIP archive..." -ForegroundColor Yellow
$zipFileName = "$modId-v$version.zip"
$zipFilePath = Join-Path $distDir $zipFileName

if (Test-Path $zipFilePath) {
    Remove-Item $zipFilePath -Force
}

Compress-Archive -Path $modStagingDir -DestinationPath $zipFilePath -Force

# Clean up staging directory
Remove-Item $stagingDir -Recurse -Force

$zipItem = Get-Item $zipFilePath
$sizeKb = [math]::Round($zipItem.Length / 1KB, 2)

Write-Host "----------------------------------------" -ForegroundColor Green
Write-Host " Release package created successfully! " -ForegroundColor Green
Write-Host " File: $($zipItem.FullName)" -ForegroundColor White
Write-Host " Size: $sizeKb KB" -ForegroundColor White
Write-Host "----------------------------------------" -ForegroundColor Green
Write-Host "Ready to upload to GitHub Releases or Nexus Mods!" -ForegroundColor Cyan
