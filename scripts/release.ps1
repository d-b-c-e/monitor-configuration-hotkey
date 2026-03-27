# MonitorProfileSwitcher Release Script
# Builds the app, creates the Inno Setup installer
# Usage: .\scripts\release.ps1 [-SkipBuild] [-InnoSetupPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"]

param(
    [switch]$SkipBuild,
    [string]$InnoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $ProjectRoot "publish"
$DistDir = Join-Path $ProjectRoot "dist"
$InstallerScript = Join-Path $ProjectRoot "installer\MonitorProfileSwitcher.iss"
$AppProject = Join-Path $ProjectRoot "MonitorProfileSwitcher\MonitorProfileSwitcher.csproj"

# Extract version from csproj
function Get-Version {
    $version = & dotnet msbuild $AppProject -getProperty:Version -nologo 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
        $version = "0.0.0"
    }
    return $version.Trim()
}

$Version = Get-Version
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "MonitorProfileSwitcher Release v$Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Create dist directory
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

# Step 1: Build
if (-not $SkipBuild) {
    Write-Host "`n[1/2] Publishing application..." -ForegroundColor Yellow

    if (Test-Path $PublishDir) {
        Remove-Item -Recurse -Force $PublishDir
    }

    & dotnet publish $AppProject -c Release -o $PublishDir --self-contained false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "Published to $PublishDir" -ForegroundColor Green
} else {
    Write-Host "`n[1/2] Skipping build (using existing publish folder)" -ForegroundColor Gray
}

# Step 2: Build installer
Write-Host "`n[2/2] Building installer..." -ForegroundColor Yellow

if (-not (Test-Path $InnoSetupPath)) {
    Write-Host "Inno Setup not found at: $InnoSetupPath" -ForegroundColor Red
    Write-Host "Install Inno Setup 6 or specify path with -InnoSetupPath" -ForegroundColor Yellow
    exit 1
}

& $InnoSetupPath /DMyAppVersion=$Version $InstallerScript
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

$InstallerName = "MonitorProfileSwitcher-$Version-Setup.exe"
Write-Host "Created: $InstallerName" -ForegroundColor Green

# Summary
Write-Host "`n========================================" -ForegroundColor Green
Write-Host "Release Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Version: $Version"
Write-Host "Output:  $DistDir"
Write-Host ""

Get-ChildItem $DistDir -Filter "MonitorProfileSwitcher-$Version*" | ForEach-Object {
    $sizeKB = [math]::Round($_.Length / 1KB, 1)
    Write-Host "  - $($_.Name) ($sizeKB KB)"
}

Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Test the installer"
Write-Host "  2. Create a git tag: git tag v$Version"
Write-Host "  3. Push the tag: git push origin v$Version"
Write-Host "  4. GitHub Actions will create the release automatically"
