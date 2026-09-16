#requires -Version 5.1
param([string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'))
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $repoPath 'artifacts\tests'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executablePath = Join-Path $testOutput 'AnalysisContractTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/r:System.Core.dll', "/out:$executablePath")
foreach ($libraryName in @('RevitAPI.dll', 'RevitAPIUI.dll', 'Newtonsoft.Json.dll')) {
    $compilerArgs += '/r:' + (Join-Path $RevitDir $libraryName)
}
$compilerArgs += @(
    (Join-Path $repoPath 'src\Tools\AnalysisTools.cs'),
    (Join-Path $repoPath 'src\Schema.cs'),
    (Join-Path $repoPath 'src\Units.cs'),
    (Join-Path $PSScriptRoot 'analysis-contract-harness.cs')
)
& $compilerPath $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Analysis contract test compilation failed.' }
& $executablePath $RevitDir
if ($LASTEXITCODE -ne 0) { throw 'Analysis contract tests failed.' }
