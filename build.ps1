#requires -Version 5.1
# Revit MCP 애드인 빌드 스크립트
# 사용법:  powershell -ExecutionPolicy Bypass -File build.ps1

param(
    [string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'),
    [Parameter(Mandatory = $true)][string]$RoslynDir
)

$ErrorActionPreference = 'Stop'

$csc      = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$srcDir   = Join-Path $PSScriptRoot 'src'
$addinsDir= Join-Path $PSScriptRoot 'artifacts\2024'
$outDir   = Join-Path $addinsDir 'RevitMcp'
$outDll   = Join-Path $outDir 'RevitMcp.dll'

# Roslyn 및 의존 DLL을 가져올 곳 (restore-dependencies.ps1 또는 사용자 지정 폴더)
$roslynSrc = (Resolve-Path -LiteralPath $RoslynDir).Path
$roslynDlls = @(
    'Microsoft.CodeAnalysis.dll',
    'Microsoft.CodeAnalysis.CSharp.dll',
    'System.Collections.Immutable.dll',
    'System.Reflection.Metadata.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Memory.dll',
    'System.Buffers.dll',
    'System.Numerics.Vectors.dll',
    'System.Threading.Tasks.Extensions.dll',
    'System.Text.Encoding.CodePages.dll'
)

Write-Host '=== Revit MCP 빌드 ===' -ForegroundColor Cyan

if (-not (Test-Path $csc))      { throw "C# 컴파일러를 찾을 수 없습니다: $csc" }
if (-not (Test-Path $revitDir)) { throw "Revit 2024 설치 폴더를 찾을 수 없습니다: $revitDir" }
foreach ($dependency in $roslynDlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $roslynSrc $dependency))) {
        throw "Missing dependency in RoslynDir: $dependency. See docs/BUILD.md."
    }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 1) 소스 파일에 UTF-8 BOM 보장
#    csc.exe 는 BOM 이 없으면 한글을 시스템 ANSI 코드페이지로 잘못 읽는다.
$sources = Get-ChildItem $srcDir -Recurse -Filter *.cs | Sort-Object FullName
if ($sources.Count -eq 0) { throw "소스 파일이 없습니다: $srcDir" }

$fixed = 0
foreach ($f in $sources) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if (-not $hasBom) {
        $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($f.FullName, $text, [System.Text.UTF8Encoding]::new($true))
        $fixed++
    }
}
Write-Host ("소스 {0}개 (BOM 보정 {1}개)" -f $sources.Count, $fixed)

# 2) 앞에서 존재 여부를 검사한 Roslyn 및 의존 DLL 복사
$roslynOk = $true
if (Test-Path $roslynSrc) {
    foreach ($d in $roslynDlls) {
        $src = Join-Path $roslynSrc $d
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $outDir $d) -Force
        } else {
            Write-Warning "Roslyn 의존 파일 없음: $d"
            $roslynOk = $false
        }
    }
} else {
    Write-Warning "Roslyn 원본 폴더가 없습니다: $roslynSrc"
    $roslynOk = $false
}
if ($roslynOk) {
    Write-Host 'Roslyn 복사 완료 - 사용자 코드는 최신 C# 로 컴파일됩니다.' -ForegroundColor Green
} else {
    Write-Warning '사용자 코드가 C# 5 (CodeDom) 로만 컴파일됩니다.'
}

$dependencyLicenses = Join-Path $roslynSrc 'licenses'
if (Test-Path -LiteralPath $dependencyLicenses) {
    $outputLicenses = Join-Path $outDir 'licenses'
    New-Item -ItemType Directory -Force -Path $outputLicenses | Out-Null
    Get-ChildItem -LiteralPath $dependencyLicenses -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $outputLicenses -Force }
}

# 3) 컴파일
$refs = @(
    (Join-Path $revitDir 'RevitAPI.dll'),
    (Join-Path $revitDir 'RevitAPIUI.dll'),
    (Join-Path $revitDir 'Newtonsoft.Json.dll'),
    (Join-Path $outDir  'Microsoft.CodeAnalysis.dll'),
    (Join-Path $outDir  'Microsoft.CodeAnalysis.CSharp.dll'),
    (Join-Path $outDir  'System.Collections.Immutable.dll'),
    # Roslyn 은 netstandard2.0 을 대상으로 하므로 파사드 참조가 필요하다.
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\netstandard.dll'),
    'System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Xml.dll'
)

$cscArgs = @('/nologo', '/target:library', '/platform:anycpu', '/optimize+', '/warn:1', "/out:$outDll")
foreach ($r in $refs) { $cscArgs += "/r:$r" }
foreach ($f in $sources) { $cscArgs += $f.FullName }

Write-Host '컴파일 중...'
$output = & $csc $cscArgs 2>&1
$code = $LASTEXITCODE

$errors = $output | Where-Object { $_ -match ': error ' }
if ($code -ne 0 -or $errors) {
    Write-Host ''
    Write-Host '=== 컴파일 실패 ===' -ForegroundColor Red
    $output | ForEach-Object { Write-Host $_ }
    throw "빌드 실패 (exit $code)"
}

$warnings = $output | Where-Object { $_ -match ': warning ' }
if ($warnings) {
    Write-Host ("경고 {0}건" -f $warnings.Count) -ForegroundColor Yellow
    $warnings | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" }
}

# 4) .addin 매니페스트
$addinPath = Join-Path $addinsDir 'RevitMcp.addin'
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitMcp</Name>
    <Assembly>RevitMcp\RevitMcp.dll</Assembly>
    <AddInId>7c3f9a21-6b48-4d5e-9f10-2a8e4c7b91d3</AddInId>
    <FullClassName>RevitMcp.App</FullClassName>
    <VendorId>HG9</VendorId>
    <VendorDescription>자체 제작 Revit MCP 서버</VendorDescription>
  </AddIn>
</RevitAddIns>
'@
[System.IO.File]::WriteAllText($addinPath, $manifest, [System.Text.UTF8Encoding]::new($false))

# 5) 기본 설정 파일 (있으면 건드리지 않는다)
$cfgPath = Join-Path $outDir 'revitmcp.config.json'
if (-not (Test-Path $cfgPath)) {
    $cfg = @'
{
  "port": 8090,
  "defaultTimeoutMs": 60000,
  "maxTimeoutMs": 600000,
  "autoStart": true
}
'@
    [System.IO.File]::WriteAllText($cfgPath, $cfg, [System.Text.UTF8Encoding]::new($false))
}

$size = [math]::Round((Get-Item $outDll).Length / 1KB, 1)
Write-Host ''
Write-Host '=== 빌드 완료 ===' -ForegroundColor Green
Write-Host "  DLL      : $outDll ($size KB)"
Write-Host "  매니페스트: $addinPath"
Write-Host "  설정     : $cfgPath"
Write-Host ''
Write-Host 'Build only: installed add-ins were not changed. See README.md for manual installation.' -ForegroundColor Cyan
