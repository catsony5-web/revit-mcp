#requires -Version 5.1
param([string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path $repoRoot ('artifacts\tests\queue-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$jsonDll = Join-Path $RevitDir 'Newtonsoft.Json.dll'
Copy-Item -LiteralPath $jsonDll -Destination $testDir
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $testDir 'RequestQueueTests.exe'
& $compiler /nologo /target:exe /r:System.Core.dll ("/r:" + $jsonDll) ("/out:" + $exe) (Join-Path $repoRoot 'src\RequestQueue.cs') (Join-Path $PSScriptRoot 'RequestQueueTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Request queue test compile failed.' }
& $exe (Join-Path $testDir 'journals')
if ($LASTEXITCODE -ne 0) { throw 'Request queue tests failed.' }
