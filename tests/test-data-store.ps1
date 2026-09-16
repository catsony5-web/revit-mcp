#requires -Version 5.1
param([string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path $repoRoot ('artifacts\tests\data-store-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$jsonDll = Join-Path $RevitDir 'Newtonsoft.Json.dll'
Copy-Item -LiteralPath $jsonDll -Destination $testDir
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $testDir 'DataStoreTests.exe'
& $compiler /nologo /target:exe /r:System.Core.dll ("/r:" + $jsonDll) ("/out:" + $exe) (Join-Path $repoRoot 'src\DataStore.cs') (Join-Path $PSScriptRoot 'DataStoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Isolated DataStore test compilation failed.' }
# DataStore derives its root from the executable location, so this cannot reach
# the installed add-in's live store. No Revit assembly/model/session is loaded.
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Isolated DataStore tests failed.' }
