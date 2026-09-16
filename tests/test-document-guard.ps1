#requires -Version 5.1
# Uses fake Autodesk types; never references RevitAPI.dll or contacts a Revit process.
param([string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $repoRoot ('artifacts\tests\document-guard-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
$jsonLibrary = Join-Path $RevitDir 'Newtonsoft.Json.dll'
Copy-Item -LiteralPath $jsonLibrary -Destination $testDirectory
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $testDirectory 'DocumentGuardTests.exe'
$compilerArguments = @('/nologo', '/langversion:5', '/target:exe', '/r:System.Core.dll', "/r:$jsonLibrary", "/out:$testExecutable")
$compilerArguments += @(
    (Join-Path $repoRoot 'src\DocumentGuard.cs'),
    (Join-Path $repoRoot 'src\Schema.cs'),
    (Join-Path $repoRoot 'src\Units.cs'),
    (Join-Path $PSScriptRoot 'DocumentGuardTests.cs')
)
& $compilerPath $compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Document guard policy test compilation failed.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Document guard policy tests failed.' }
