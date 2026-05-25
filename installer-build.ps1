<#
.SYNOPSIS
    Automated MSI Build Script for VianaRego Edge Agent
.DESCRIPTION
    Builds the Agent, Sidecars, and MSI Installer in Release mode.
#>

$ErrorActionPreference = "Stop"
$solutionRoot = $PSScriptRoot
$installerProject = "$solutionRoot\VianaRego.Installer\VianaRego.Installer.wixproj"

Write-Host "Starting MSI Build Process..." -ForegroundColor Cyan

# 1. Clean
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean $installerProject -c Release
Remove-Item "$solutionRoot\VianaRego.Installer\Release", "$solutionRoot\VianaRego.Installer\bin", "$solutionRoot\VianaRego.Installer\obj" -Recurse -Force -ErrorAction SilentlyContinue

# 2. Build Agent (Triggers sidecars via dependencies)
Write-Host "Building Edge Agent Core..." -ForegroundColor Yellow
dotnet build "$solutionRoot\src\VianaRego.Edge.Agent\VianaRego.Edge.Agent.csproj" -c Release

# 3. Build Installer
Write-Host "Building MSI Installer..." -ForegroundColor Yellow
dotnet build $installerProject -c Release

# 4. Success Verification
$msiPath = "$solutionRoot\VianaRego.Installer\Release\VianaRego.Installer.msi"
if (Test-Path $msiPath) {
    Write-Host ""
    Write-Host "MSI Build Successful!" -ForegroundColor Green
    Write-Host "Path: $msiPath" -ForegroundColor Gray
} else {
    Write-Error "MSI build failed. File not found at expected path."
}
