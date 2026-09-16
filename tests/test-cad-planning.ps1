#requires -Version 5.1
param([string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $repoRoot ('artifacts\tests\cad-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$json = Join-Path $RevitDir 'Newtonsoft.Json.dll'
Copy-Item -LiteralPath $json -Destination $testRoot
$exe = Join-Path $testRoot 'CadPlanningTests.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /langversion:5 /target:exe "/out:$exe" "/r:$json" /r:System.Core.dll (Join-Path $repoRoot 'src\CadPlanning.cs') (Join-Path $PSScriptRoot 'test-cad-planning.cs')
if ($LASTEXITCODE -ne 0) { throw 'CAD planning test compilation failed.' }
& $exe (Join-Path $testRoot 'drawings')
if ($LASTEXITCODE -ne 0) { throw 'CAD planning tests failed.' }
