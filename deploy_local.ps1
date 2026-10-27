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

if (-not $NoBuild) {
    Write-Host "[Deploy] Building AITraffic in $Configuration configuration..." -ForegroundColor Yellow
    
    # Locate best MSBuild (VS2022 > VS Build Tools > .NET Framework)
    $msbuildPath = $null
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found -and (Test-Path $found)) {
            $msbuildPath = $found
        }
    }
    if (-not $msbuildPath) {
        $vs2022Candidate = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $vs2022Candidate) {
            $msbuildPath = $vs2022Candidate
        }
    }
    if (-not $msbuildPath) {
        $msbuildPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    }
    if (-not (Test-Path $msbuildPath)) {
        $msbuildPath = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    }

    Write-Host "[Deploy] Using MSBuild: $msbuildPath" -ForegroundColor Cyan
    $csprojPath = Join-Path $scriptDir "AITraffic.csproj"
    & $msbuildPath "$csprojPath" /p:Configuration=$Configuration /p:DVInstallPath="$dvBase" /t:Rebuild /verbosity:minimal /nologo

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

$iniPath = Join-Path $scriptDir "settings.ini"
$destIniPath = Join-Path $destDir "settings.ini"
if ((Test-Path $iniPath) -and (-not (Test-Path $destIniPath))) {
    Copy-Item $iniPath -Destination $destDir
    Write-Host "[Deploy] Deployed default settings.ini" -ForegroundColor Cyan
}

$deployedItem = Get-Item (Join-Path $destDir "AITraffic.dll")
Write-Host "[Deploy] Successfully deployed to $destDir" -ForegroundColor Green
Write-Host "[Deploy] DLL LastWriteTime: $($deployedItem.LastWriteTime)" -ForegroundColor Cyan
