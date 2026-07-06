<#
.SYNOPSIS
    Builds the APG Revit Plugins suite and installs it for the selected Revit versions.

.DESCRIPTION
    For each requested Revit version this script:
      1. Builds src/CodeCompliance with the matching configuration (e.g. "Release R25").
      2. Copies CodeCompliance.dll into  %APPDATA%\Autodesk\Revit\Addins\<version>\CodeCompliance\
      3. Copies CodeCompliance.addin into %APPDATA%\Autodesk\Revit\Addins\<version>\

    Run from a terminal with the .NET SDK installed (comes with Visual Studio 2022+):
        powershell -ExecutionPolicy Bypass -File install\install.ps1
        powershell -ExecutionPolicy Bypass -File install\install.ps1 -RevitVersions 2025
        powershell -ExecutionPolicy Bypass -File install\install.ps1 -RevitVersions 2024,2026 -Configuration Debug

.PARAMETER RevitVersions
    One or more of: 2024, 2025, 2026, 2027. Default: all of them.

.PARAMETER Configuration
    Debug or Release. Default: Release.

.PARAMETER SkipBuild
    Skip the build step and install whatever is already in the bin folder.
#>
param(
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string[]]$RevitVersions = @("2024", "2025", "2026", "2027"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "src\CodeCompliance"
$addinFile  = Join-Path $PSScriptRoot "CodeCompliance.addin"

foreach ($version in $RevitVersions) {
    $shortVersion = "R" + $version.Substring(2)          # 2025 -> R25
    $buildConfig  = "$Configuration $shortVersion"        # e.g. "Release R25"
    $outputDir    = Join-Path $projectDir "bin\$buildConfig"
    $addinsDir    = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    $targetDir    = Join-Path $addinsDir "CodeCompliance"

    Write-Host ""
    Write-Host "=== Revit $version ($buildConfig) ===" -ForegroundColor Cyan

    if (-not $SkipBuild) {
        Write-Host "Building..."
        dotnet build $projectDir -c $buildConfig
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build failed for Revit $version. Aborting."
        }
    }

    $dll = Join-Path $outputDir "CodeCompliance.dll"
    if (-not (Test-Path $dll)) {
        Write-Error "Build output not found: $dll"
    }

    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item $dll -Destination $targetDir -Force
    $pdb = Join-Path $outputDir "CodeCompliance.pdb"
    if (Test-Path $pdb) { Copy-Item $pdb -Destination $targetDir -Force }
    Copy-Item $addinFile -Destination $addinsDir -Force

    Write-Host "Installed to $addinsDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Start Revit - you should see an 'APG Revit Plugins' ribbon tab." -ForegroundColor Green
Write-Host "Revit will ask once whether to load the add-in; choose 'Always Load'."
