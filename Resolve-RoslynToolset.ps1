<#
.SYNOPSIS
    Shared MSBuild discovery, Roslyn C# 6+ fallback resolution, and verified build execution
    for deploy_local.ps1 and package_release.ps1.
#>

function Find-AITrafficMSBuild {
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

    return $msbuildPath
}

<#
    Resolves a Roslyn (C# 6+) csc.exe toolset folder, provisioning it from NuGet on demand.
    Required because the in-box .NET Framework MSBuild routes compilation to a C# 5-only
    compiler, which rejects string interpolation and expression-bodied members used here.
#>
function Resolve-RoslynCscToolPath {
    param(
        [string]$Version = "4.14.0",
        [switch]$NoDownload
    )

    $toolsetRoot = Join-Path $env:LOCALAPPDATA "RoslynToolset"
    $expected = Join-Path $toolsetRoot (Join-Path $Version "tasks\net472")

    if (Test-Path (Join-Path $expected "csc.exe")) {
        Write-Host "[Build] Using Roslyn toolset: $expected" -ForegroundColor Cyan
        return $expected
    }

    if (Test-Path $toolsetRoot) {
        $cached = Get-ChildItem $toolsetRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $cscDir = Join-Path $_.FullName "tasks\net472"
            if (Test-Path (Join-Path $cscDir "csc.exe")) {
                $parsed = $null
                [void][version]::TryParse($_.Name, [ref]$parsed)
                [pscustomobject]@{
                    Path    = $cscDir
                    Version = $(if ($parsed) { $parsed } else { [version]'0.0' })
                }
            }
        } | Sort-Object Version -Descending | Select-Object -First 1

        if ($cached) {
            Write-Host "[Build] Using cached Roslyn toolset: $($cached.Path)" -ForegroundColor Cyan
            return $cached.Path
        }
    }

    if ($NoDownload) {
        Write-Error "No C# 6+ (Roslyn) compiler found and -NoRoslynDownload was specified. Install the .NET SDK/Visual Studio Build Tools, or allow the NuGet fallback."
    }

    Write-Host "[Build] No C# 6+ compiler found; provisioning Roslyn $Version from NuGet..." -ForegroundColor Yellow
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch {
        Write-Verbose "Could not raise TLS protocol version: $_"
    }

    $zipPath = Join-Path $env:TEMP "microsoft.net.compilers.toolset.$Version.zip"
    $uri = "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$Version/microsoft.net.compilers.toolset.$Version.nupkg"

    Invoke-WebRequest -Uri $uri -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath (Join-Path $toolsetRoot $Version) -Force
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path (Join-Path $expected "csc.exe"))) {
        Write-Error "Roslyn toolset provisioning failed: csc.exe not found in $expected"
    }

    Write-Host "[Build] Roslyn toolset ready: $expected" -ForegroundColor Green
    return $expected
}

function Invoke-AITrafficBuild {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptDir,
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$DVInstallPath,
        [Parameter(Mandatory = $true)][string]$ExpectedDllPath,
        [string]$RoslynVersion = "4.14.0",
        [string]$CscToolPath = $null,
        [switch]$NoRoslynDownload
    )

    $msbuildPath = Find-AITrafficMSBuild
    Write-Host "[Build] Using MSBuild: $msbuildPath" -ForegroundColor Cyan

    if (-not $CscToolPath -and $env:CscToolPath) {
        $CscToolPath = $env:CscToolPath
    }

    $inBoxMsBuildRoot = Join-Path $env:WINDIR "Microsoft.NET"
    $usesInBoxMsBuild = $msbuildPath.StartsWith($inBoxMsBuildRoot, [StringComparison]::OrdinalIgnoreCase)

    $compilerArgs = @()
    if (-not $CscToolPath -and $usesInBoxMsBuild) {
        $CscToolPath = Resolve-RoslynCscToolPath -Version $RoslynVersion -NoDownload:$NoRoslynDownload
    }
    if ($CscToolPath) {
        if (-not (Test-Path (Join-Path $CscToolPath "csc.exe"))) {
            Write-Error "csc.exe was not found in the specified CscToolPath: $CscToolPath"
        }
        Write-Host "[Build] Roslyn C# compiler: $(Join-Path $CscToolPath 'csc.exe')" -ForegroundColor Cyan
        $compilerArgs += "/p:CscToolPath=$CscToolPath"
        $compilerArgs += "/p:CscToolExe=csc.exe"
        $compilerArgs += "/p:LangVersion=latest"
    }

    # Marker on the same volume as the build outputs, so the post-build freshness check cannot be
    # fooled by a skipped compile that silently re-copies a stale assembly out of obj/.
    $objDir = Join-Path $ScriptDir "obj"
    if (-not (Test-Path $objDir)) {
        New-Item -ItemType Directory -Path $objDir -Force | Out-Null
    }
    $buildMarker = Join-Path $objDir ".build_marker_$Configuration"
    Set-Content -Path $buildMarker -Value "build-start"
    $buildStartedAt = (Get-Item $buildMarker).LastWriteTime

    $msbuildArgs = @(
        "$ProjectPath",
        "/p:Configuration=$Configuration",
        "/p:DVInstallPath=$DVInstallPath",
        "/t:Rebuild",
        "/verbosity:minimal",
        "/nologo"
    ) + $compilerArgs

    try {
        & $msbuildPath @msbuildArgs
        $msbuildExitCode = $LASTEXITCODE
    } finally {
        Remove-Item $buildMarker -Force -ErrorAction SilentlyContinue
    }

    if ($msbuildExitCode -ne 0) {
        Write-Error "Build failed with exit code $msbuildExitCode"
    }

    if (-not (Test-Path $ExpectedDllPath)) {
        Write-Error "Build reported success but no assembly was produced at $ExpectedDllPath"
    }
    if ((Get-Item $ExpectedDllPath).LastWriteTime -lt $buildStartedAt.AddSeconds(-2)) {
        Write-Error "Stale artifact detected: $ExpectedDllPath was not regenerated by this build (compilation was skipped). Delete bin/ and obj/ and retry, or verify the C# compiler in use."
    }
}
