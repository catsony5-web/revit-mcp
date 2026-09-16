#requires -Version 5.1
param(
    [switch]$Install,
    [string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024')
)
$ErrorActionPreference = 'Stop'
foreach ($fileName in @('RevitAPI.dll','RevitAPIUI.dll','Newtonsoft.Json.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RevitDir $fileName))) { throw "Revit 2024 dependency not found: $fileName. Check -RevitDir." }
}
if ($Install -and (Get-Process -Name Revit -ErrorAction SilentlyContinue)) {
    throw 'Save your work and close all Revit windows before installing. Use setup.ps1 without -Install to build only.'
}
& (Join-Path $PSScriptRoot 'restore-dependencies.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -RevitDir $RevitDir -RoslynDir (Join-Path $PSScriptRoot 'deps\roslyn')
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-protocol.ps1') -RevitDir $RevitDir
if ($LASTEXITCODE -ne 0) { throw 'Protocol validation failed. Installation was not attempted.' }
if ($Install) { & (Join-Path $PSScriptRoot 'install.ps1') }
else { Write-Host 'Build and protocol checks complete. To install later, close Revit and run install.ps1.' }
