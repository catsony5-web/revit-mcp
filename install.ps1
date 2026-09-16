#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param([string]$AddinsDir = (Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'))
$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $PSScriptRoot 'artifacts\2024'
$sourceDir = Join-Path $buildRoot 'RevitMcp'
foreach ($relativePath in @('RevitMcp.addin','RevitMcp\RevitMcp.dll','RevitMcp\revitmcp.config.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $relativePath))) { throw 'Build artifacts missing. Run setup.ps1 first.' }
}
$targetRoot = [IO.Path]::GetFullPath($AddinsDir)
$targetDir = Join-Path $targetRoot 'RevitMcp'
$manifestPath = Join-Path $targetRoot 'RevitMcp.addin'
if (-not $PSCmdlet.ShouldProcess($targetRoot, 'Back up existing RevitMcp and install the local build')) { return }
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { throw 'Save your work and close all Revit windows before installing.' }

# Back up before writing. Do not remove old files or another add-in.
if ((Test-Path -LiteralPath $targetDir) -or (Test-Path -LiteralPath $manifestPath)) {
    $backupName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $backupDir = Join-Path (Join-Path $PSScriptRoot 'backups') $backupName
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    if (Test-Path -LiteralPath $targetDir) { Copy-Item -LiteralPath $targetDir -Destination $backupDir -Recurse }
    if (Test-Path -LiteralPath $manifestPath) { Copy-Item -LiteralPath $manifestPath -Destination $backupDir }
    Write-Host ('Backup: ' + $backupDir)
}
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Get-ChildItem -LiteralPath $sourceDir -Filter '*.dll' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $targetDir -Force }
$sourceLicenses = Join-Path $sourceDir 'licenses'
if (Test-Path -LiteralPath $sourceLicenses) {
    $targetLicenses = Join-Path $targetDir 'licenses'
    New-Item -ItemType Directory -Path $targetLicenses -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceLicenses -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $targetLicenses -Force }
}
$configPath = Join-Path $targetDir 'revitmcp.config.json'
if (-not (Test-Path -LiteralPath $configPath)) { Copy-Item -LiteralPath (Join-Path $sourceDir 'revitmcp.config.json') -Destination $configPath }
Copy-Item -LiteralPath (Join-Path $buildRoot 'RevitMcp.addin') -Destination $manifestPath -Force
Write-Host ('Installed in: ' + $targetRoot)
Write-Host 'Start Revit, check the MCP ribbon, then configure your HTTP MCP client using the port in revitmcp.config.json (default 8090).'
