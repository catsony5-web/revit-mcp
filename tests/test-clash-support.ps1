#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $repo 'artifacts\tests\clash-support'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'ClashSupportTests.exe'
& $compiler /nologo /target:exe /warnaserror+ /out:$exe (Join-Path $repo 'src\ClashSupport.cs') (Join-Path $PSScriptRoot 'ClashSupportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clash helper test compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Clash helper tests failed.' }
