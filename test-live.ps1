#requires -Version 5.1
# Revit 이 떠 있는 상태에서 MCP 서버를 실제로 두드려 본다.
# 사용법:  powershell -ExecutionPolicy Bypass -File test-live.ps1 [-Port 8090]

param([int]$Port = 8090, [switch]$AllowLiveTest)

if (-not $AllowLiveTest) {
    throw 'This test queries Revit, executes C#, and writes test data to the local store. Use an isolated test model and pass -AllowLiveTest.'
}

$ErrorActionPreference = 'Continue'
$base = "http://127.0.0.1:$Port"
$mcp  = "$base/mcp"
$id   = 0
$fail = 0

function Check($name, $cond, $detail) {
    if ($cond) { Write-Host ("  [OK]   " + $name) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red; $script:fail++ }
}

function Rpc($method, $params) {
    $script:id++
    $body = @{ jsonrpc = '2.0'; id = $script:id; method = $method }
    if ($params) { $body.params = $params }
    $json = $body | ConvertTo-Json -Depth 20 -Compress
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $res = Invoke-WebRequest -Uri $mcp -Method Post -Body $bytes `
                 -ContentType 'application/json' -UseBasicParsing -TimeoutSec 120
        if (-not $res.Content) { return $null }
        return $res.Content | ConvertFrom-Json
    } catch {
        Write-Host ("  요청 실패: " + $_.Exception.Message) -ForegroundColor Red
        return $null
    }
}

function ToolText($resp) {
    if ($null -eq $resp -or $null -eq $resp.result -or $null -eq $resp.result.content) { return '' }
    return $resp.result.content[0].text
}

function GuardedCode($code) {
    $context = Rpc 'tools/call' @{ name = 'get_document_context'; arguments = @{} }
    if ($context.result.isError -ne $false -or $context.result._meta.status -ne 'succeeded') { throw 'Document context did not complete; do not execute code.' }
    $document = (ToolText $context | ConvertFrom-Json).active
    if (-not $document.expectedDocument) { throw 'No active document token.' }
    return Rpc 'tools/call' @{ name = 'send_code_to_revit'; arguments = @{
        code = $code; transactionMode = 'none'; requestId = [guid]::NewGuid().ToString('N')
        expectedDocument = $document.expectedDocument; expectedRevision = $document.expectedRevision
    } }
}

Write-Host "=== Revit MCP 실제 연결 검증 ($mcp) ===" -ForegroundColor Cyan
Write-Host ''

# 0) 헬스체크
Write-Host '--- /health ---'
try {
    $h = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 10
    Check "서버 응답" ($h.status -eq 'ok') ($h | ConvertTo-Json -Compress)
    Write-Host ("  등록 도구: " + $h.tools + "개")
} catch {
    Write-Host ''
    Write-Host "서버에 연결하지 못했습니다: $base" -ForegroundColor Red
    Write-Host '확인할 것:' -ForegroundColor Yellow
    Write-Host '  1. Revit 2024 가 실행 중인가'
    Write-Host '  2. 리본 [부가 기능] > MCP > "MCP 상태" 가 실행 중이라고 나오는가'
    Write-Host '  3. 스마트 앱 제어가 애드인을 막지 않았는가 (이벤트 뷰어 CodeIntegrity)'
    exit 1
}

# 1) 핸드셰이크
Write-Host ''
Write-Host '--- initialize ---'
$r = Rpc 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'test-live'; version = '1' } }
Check "initialize 응답" ($null -ne $r.result) ($r | ConvertTo-Json -Compress)
Check "serverInfo.name" ($r.result.serverInfo.name -eq 'revit-mcp') ''
Write-Host ("  프로토콜: " + $r.result.protocolVersion)

# 2) 도구 목록
Write-Host ''
Write-Host '--- tools/list ---'
$r = Rpc 'tools/list' $null
$tools = $r.result.tools
Check "도구 목록 수신" ($null -ne $tools) ''
Write-Host ("  도구 " + $tools.Count + "개")

# 3) say_hello
Write-Host ''
Write-Host '--- say_hello ---'
$r = Rpc 'tools/call' @{ name = 'say_hello'; arguments = @{ message = '연결 확인' } }
$t = ToolText $r
Check "say_hello 성공" ($r.result.isError -eq $false) $t
if ($t) {
    $info = $t | ConvertFrom-Json
    Write-Host ("  Revit      : " + $info.revitVersion + " (" + $info.revitBuild + ")")
    Write-Host ("  문서       : " + $info.documentTitle)
    Write-Host ("  활성 뷰    : " + $info.activeView)
    Write-Host ("  컴파일러   : " + $info.codeCompiler)
    Check "Revit 문서가 열려 있음" ($null -ne $info.documentTitle) '문서를 열고 다시 실행하십시오.'
}

# 4) 읽기 전용 조회
Write-Host ''
Write-Host '--- get_current_view_info ---'
$r = Rpc 'tools/call' @{ name = 'get_current_view_info'; arguments = @{} }
$t = ToolText $r
Check "뷰 정보 조회 성공" ($r.result.isError -eq $false) $t
if ($r.result.isError -eq $false) {
    $v = $t | ConvertFrom-Json
    Write-Host ("  뷰: " + $v.name + " (" + $v.viewType + "), 축척 1:" + $v.scale)
}

# 5) 코드 실행 - 최신 C# 문법으로 Roslyn 경로를 확인한다 (읽기 전용)
Write-Host ''
Write-Host '--- send_code_to_revit (읽기 전용, 최신 C# 문법) ---'
$code = @'
var n = new FilteredElementCollector(document).WhereElementIsNotElementType().GetElementCount();
var walls = new FilteredElementCollector(document)
    .OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().GetElementCount();
return $"요소 {n}개, 벽 {walls}개";
'@
$r = GuardedCode $code
$t = ToolText $r
Check "코드 실행 성공" ($r.result.isError -eq $false) $t
if ($r.result.isError -eq $false) {
    $c = $t | ConvertFrom-Json
    Write-Host ("  결과: " + $c.result)
    Write-Host ("  소요: " + $c.elapsedMs + " ms, 컴파일러: " + $c.compiler)
    Check "문자열 보간(C# 6+) 이 컴파일됨" ($c.result -match '요소') $c.result
}

# 6) 컴파일 오류가 제대로 보고되는지
Write-Host ''
Write-Host '--- 컴파일 오류 보고 ---'
$r = GuardedCode 'this is not valid C#;'
$t = ToolText $r
Check "잘못된 코드는 isError=true" ($r.result.isError -eq $true) $t
Check "오류 메시지에 줄 번호 포함" ($t -match '줄|error CS') $t

# 7) 로컬 저장소
Write-Host ''
Write-Host '--- 로컬 저장소 왕복 ---'
$r = Rpc 'tools/call' @{ name = 'store_project_data'; arguments = @{ project_name = '__연결테스트__'; project_number = 'T-001' } }
Check "store_project_data" ($r.result.isError -eq $false) (ToolText $r)
$r = Rpc 'tools/call' @{ name = 'query_stored_data'; arguments = @{ query_type = 'project_by_name'; project_name = '__연결테스트__' } }
$t = ToolText $r
Check "query_stored_data 되읽기" ($t -match 'T-001') $t

Write-Host ''
if ($fail -eq 0) { Write-Host '=== 전부 통과 ===' -ForegroundColor Green; exit 0 }
else { Write-Host ("=== 실패 " + $fail + "건 ===") -ForegroundColor Red; exit 1 }
