<#
.SYNOPSIS
    Removes the Code Compliance add-in from the selected Revit versions.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install\uninstall.ps1
    powershell -ExecutionPolicy Bypass -File install\uninstall.ps1 -RevitVersions 2025
#>
param(
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string[]]$RevitVersions = @("2024", "2025", "2026", "2027")
)

$ErrorActionPreference = "Stop"

foreach ($version in $RevitVersions) {
    $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    $removed = $false

    $manifest = Join-Path $addinsDir "CodeCompliance.addin"
    if (Test-Path $manifest) {
        Remove-Item $manifest -Force
        $removed = $true
    }

    $folder = Join-Path $addinsDir "CodeCompliance"
    if (Test-Path $folder) {
        Remove-Item $folder -Recurse -Force
        $removed = $true
    }

    if ($removed) {
        Write-Host "Removed add-in from Revit $version." -ForegroundColor Green
    } else {
        Write-Host "Nothing to remove for Revit $version."
    }
}
