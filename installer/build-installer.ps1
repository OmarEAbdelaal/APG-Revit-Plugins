<#
.SYNOPSIS
    Builds the APG Revit Plugins suite for all Revit versions and compiles the
    single user installer (.exe).

.DESCRIPTION
    1. Builds src/CodeCompliance in Release for every Revit version (2024-2027).
       If a single version fails to build (e.g. its API package is not on NuGet yet),
       it is skipped with a warning and the installer is produced without it.
    2. Compiles installer/CodeCompliance.iss with Inno Setup 6.
       Output: installer/output/APG-Revit-Plugins-Setup-<version>.exe

    Requirements: .NET SDK 8+ and Inno Setup 6 (https://jrsoftware.org/isdl.php,
    or: winget install JRSoftware.InnoSetup)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "src\CodeCompliance"
$issFile    = Join-Path $PSScriptRoot "CodeCompliance.iss"

# --- 1. Build every Revit target ---------------------------------------------
$built = @()
if (-not $SkipBuild) {
    foreach ($shortVersion in @("R24", "R25", "R26", "R27")) {
        $config = "Release $shortVersion"
        Write-Host ""
        Write-Host "=== Building $config ===" -ForegroundColor Cyan
        dotnet build $projectDir -c $config
        if ($LASTEXITCODE -eq 0) {
            $built += $config
        } else {
            Write-Warning "Build failed for '$config' - it will be excluded from the installer."
        }
    }
    if ($built.Count -eq 0) {
        Write-Error "No configuration built successfully; cannot create an installer."
    }
}

# --- 2. Locate Inno Setup ------------------------------------------------------
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Error ("Inno Setup 6 (ISCC.exe) not found. Install it with: " +
                 "winget install JRSoftware.InnoSetup  (or from https://jrsoftware.org/isdl.php)")
}

# --- 3. Compile the installer --------------------------------------------------
Write-Host ""
Write-Host "=== Compiling installer ===" -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed."
}

$output = Get-ChildItem (Join-Path $PSScriptRoot "output") -Filter "*.exe" |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ""
Write-Host "Installer created: $($output.FullName)" -ForegroundColor Green
if ($built.Count -gt 0) {
    Write-Host "Included Revit targets: $($built -join ', ')"
}
