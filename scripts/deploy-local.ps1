# Deploy a locally published trial build over the existing Program Files installation.
# Run from an elevated PowerShell process after `dotnet publish -o publish`.
#Requires -RunAsAdministrator
param()

$ErrorActionPreference = 'Stop'
$sourceDir = (Resolve-Path (Join-Path $PSScriptRoot '..\publish')).Path
$installDir = 'C:\Program Files\MonitorProfileSwitcher'
$installedExe = Join-Path $installDir 'MonitorProfileSwitcher.exe'
$fileNames = @(
    'MonitorProfileSwitcher.deps.json',
    'MonitorProfileSwitcher.dll',
    'MonitorProfileSwitcher.exe',
    'MonitorProfileSwitcher.pdb',
    'MonitorProfileSwitcher.runtimeconfig.json'
)

if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
    throw "The expected installed copy was not found at $installedExe."
}
foreach ($name in $fileNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $name) -PathType Leaf)) {
        throw "Missing published file: $name"
    }
}

$backupRoot = Join-Path $env:LOCALAPPDATA 'MonitorProfileSwitcher\backups'
$backupDir = Join-Path $backupRoot ('installed-before-update-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
foreach ($name in $fileNames) {
    $installedFile = Join-Path $installDir $name
    if (Test-Path -LiteralPath $installedFile -PathType Leaf) {
        Copy-Item -LiteralPath $installedFile -Destination (Join-Path $backupDir $name)
    }
}

$running = Get-CimInstance Win32_Process -Filter "Name = 'MonitorProfileSwitcher.exe'" |
    Where-Object { $_.ExecutablePath -eq $installedExe }
foreach ($instance in $running) {
    Stop-Process -Id $instance.ProcessId -Force
}
foreach ($instance in $running) {
    if (Get-Process -Id $instance.ProcessId -ErrorAction SilentlyContinue) {
        try { Wait-Process -Id $instance.ProcessId -Timeout 10 -ErrorAction Stop }
        catch { throw "Installed process $($instance.ProcessId) did not exit; deployment stopped. Backup: $backupDir" }
    }
}

try {
    foreach ($name in $fileNames) {
        Copy-Item -LiteralPath (Join-Path $sourceDir $name) -Destination (Join-Path $installDir $name) -Force
    }
}
catch {
    foreach ($name in $fileNames) {
        $backupFile = Join-Path $backupDir $name
        if (Test-Path -LiteralPath $backupFile -PathType Leaf) {
            Copy-Item -LiteralPath $backupFile -Destination (Join-Path $installDir $name) -Force
        }
    }
    throw
}

Write-Output "Installed trial build. Previous files: $backupDir"
