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

# 0. Pre-Build Static Gatekeeper: Verify Architecture Invariants & Anti-Patterns
$verifierScript = Join-Path $scriptDir "verify_invariants.py"
if (Test-Path $verifierScript) {
    Write-Host "[Gatekeeper] Running static invariant verification (verify_invariants.py)..." -ForegroundColor Cyan
    & py "$verifierScript"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Static invariant verification failed with exit code $LASTEXITCODE. Aborting release packaging."
        exit $LASTEXITCODE
    }
    Write-Host "[Gatekeeper] Invariant verification passed.`n" -ForegroundColor Green
}

# Auto-detect Derail Valley install path
$dvBase = "C:\Program Files (x86)\Steam\steamapps\common\Derail Valley"
if (-not (Test-Path $dvBase)) {
    $dvBase = "J:\SteamLibrary\steamapps\common\Derail Valley"
}

# 1. Build project in Release mode (unless -NoBuild is specified)
if (-not $NoBuild) {
    Write-Host "[1/3] Building $modId in Release configuration..." -ForegroundColor Yellow
    
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

    if (-not (Test-Path $msbuildPath)) {
        Write-Error "Could not locate MSBuild.exe on this system."
    }

    Write-Host "[Build] Using MSBuild: $msbuildPath" -ForegroundColor Cyan
    $csprojPath = Join-Path $scriptDir "$modId.csproj"
    & $msbuildPath "$csprojPath" /p:Configuration=Release /p:DVInstallPath="$dvBase" /t:Rebuild /verbosity:minimal /nologo

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

# Use tar.exe (built-in on Windows 10/11) or Python to ensure POSIX '/' path separators in ZIP entries.
# Standard Windows Compress-Archive uses '\' which breaks UMM updater and Linux/Steam Deck extractions.
$tarCmd = Get-Command "tar.exe" -ErrorAction SilentlyContinue
if ($tarCmd) {
    & $tarCmd.Source -a -c -f $zipFilePath -C $stagingDir $modId
    if ($LASTEXITCODE -ne 0) {
        Write-Error "tar.exe failed with exit code $LASTEXITCODE"
    }
} else {
    py -c "import zipfile, os, sys; staging, mod, out = sys.argv[1], sys.argv[2], sys.argv[3]; zf = zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED); [zf.write(os.path.join(r, f), os.path.relpath(os.path.join(r, f), staging).replace('\\', '/')) for r, _, fs in os.walk(os.path.join(staging, mod)) for f in fs]; zf.close()" "$stagingDir" "$modId" "$zipFilePath"
}

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
