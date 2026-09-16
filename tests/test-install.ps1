#requires -Version 5.1
# Filesystem-only checks. All destinations and fake build files stay under artifacts/tests.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $repoRoot ('artifacts\tests\install-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $testRoot 'fixture'
$fakeBuild = Join-Path $fixture 'artifacts\2024\RevitMcp'
$target = Join-Path $testRoot 'addins'
New-Item -ItemType Directory -Path $fakeBuild -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'install.ps1') -Destination $fixture
Set-Content -LiteralPath (Join-Path $fakeBuild 'RevitMcp.dll') -Value 'fake-binary-v1'
Set-Content -LiteralPath (Join-Path $fakeBuild 'revitmcp.config.json') -Value '{"port":8090}'
Set-Content -LiteralPath (Join-Path (Split-Path $fakeBuild -Parent) 'RevitMcp.addin') -Value '<fake-manifest />'
$installer = Join-Path $fixture 'install.ps1'
$script:simulateRunning = $false
function Get-Process { param($Name,$ErrorAction) if ($simulateRunning) { [pscustomobject]@{ProcessName='Revit'} } }
function Assert($condition,$label) { if (-not $condition) { throw $label }; Write-Host ('PASS: ' + $label) }

& $installer -AddinsDir $target -WhatIf
Assert (-not (Test-Path -LiteralPath $target)) 'WhatIf writes nothing'
$script:simulateRunning = $true
$blocked = $false
try { & $installer -AddinsDir $target } catch { $blocked = $_.Exception.Message -match 'close all Revit' }
Assert ($blocked -and -not (Test-Path -LiteralPath $target)) 'Running Revit prevents installation'
$script:simulateRunning = $false
& $installer -AddinsDir $target
Assert (Test-Path -LiteralPath (Join-Path $target 'RevitMcp\RevitMcp.dll')) 'Fresh installation copies DLL'
Assert (Test-Path -LiteralPath (Join-Path $target 'RevitMcp.addin')) 'Fresh installation copies manifest'
Set-Content -LiteralPath (Join-Path $target 'RevitMcp\revitmcp.config.json') -Value '{"port":9999}'
New-Item -ItemType Directory -Path (Join-Path $target 'RevitMcp\RevitMcpData') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $target 'RevitMcp\RevitMcpData\example.json') -Value '{"preserve":true}'
Set-Content -LiteralPath (Join-Path $target 'other.addin') -Value 'unrelated-addin'
Set-Content -LiteralPath (Join-Path $fakeBuild 'RevitMcp.dll') -Value 'fake-binary-v2'
& $installer -AddinsDir $target
Assert ((Get-Content -LiteralPath (Join-Path $target 'RevitMcp\RevitMcp.dll') -Raw).Trim() -eq 'fake-binary-v2') 'Upgrade updates DLL'
Assert ((Get-Content -LiteralPath (Join-Path $target 'RevitMcp\revitmcp.config.json') -Raw) -match '9999') 'Upgrade preserves configuration'
Assert (Test-Path -LiteralPath (Join-Path $target 'RevitMcp\RevitMcpData\example.json')) 'Upgrade preserves stored data'
Assert ((Get-Content -LiteralPath (Join-Path $target 'other.addin') -Raw).Trim() -eq 'unrelated-addin') 'Other add-ins remain unchanged'
$backups = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'backups') -Directory)
Assert ($backups.Count -eq 1) 'Upgrade creates one backup'
Assert ((Get-Content -LiteralPath (Join-Path $backups[0].FullName 'RevitMcp\RevitMcp.dll') -Raw).Trim() -eq 'fake-binary-v1') 'Backup contains previous DLL'
Write-Host '10 filesystem checks passed. No installed add-in or Revit process was accessed.'
