#requires -Version 5.1
# Revit 없이 MCP 프로토콜 계층만 검증한다.
# 도구 등록, tools/list 스키마 생성, initialize 응답, 오류 처리를 확인한다.
# Revit API 를 호출하지는 않으므로 실제 모델 동작은 Revit 안에서 따로 확인해야 한다.

param([string]$RevitDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024'))

$ErrorActionPreference = 'Stop'

$outDir  = Join-Path $PSScriptRoot 'artifacts\2024\RevitMcp'
$revit   = $RevitDir
$fail    = 0
$checks  = 0

function Check($name, $cond, $detail) {
    $script:checks++
    if ($cond) {
        Write-Host ("  [OK]   " + $name) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red
        $script:fail++
    }
}

Write-Host '=== MCP 프로토콜 검증 ===' -ForegroundColor Cyan

# Revit 밖에서는 형제 어셈블리를 스스로 찾지 못한다.
# Revit 설치 폴더와 애드인 폴더를 뒤지는 해결기를 달아둔다.
$probeDirs = @($revit, $outDir)
$resolver = [System.ResolveEventHandler] {
    param($sender, $e)
    $simple = ($e.Name -split ',')[0]
    foreach ($dir in $probeDirs) {
        $p = Join-Path $dir ($simple + '.dll')
        if (Test-Path $p) {
            try { return [System.Reflection.Assembly]::LoadFrom($p) } catch { }
        }
    }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

# 네이티브 의존성 탐색을 위해 Revit 폴더를 PATH 에 얹는다.
$env:PATH = $revit + ';' + $env:PATH

$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $outDir 'RevitMcp.dll'))
Write-Host ("어셈블리 로드: " + $asm.GetName().Name)

$flags = [System.Reflection.BindingFlags]'Static,NonPublic,Public'

# 1) 도구 등록
$allTools = $asm.GetType('RevitMcp.Tools.AllTools')
$allTools.GetMethod('RegisterAll', $flags).Invoke($null, @())

$registry = $asm.GetType('RevitMcp.ToolRegistry')
$count = $registry.GetProperty('Count', $flags).GetValue($null)
Write-Host ''
Write-Host ("등록된 도구: " + $count + "개")
Check "도구가 47개 등록됨" ($count -eq 47) ("실제: " + $count)

# 2) JSON-RPC 진입점
$proto  = $asm.GetType('RevitMcp.McpProtocol')
$handle = $proto.GetMethod('Handle', $flags)

function Rpc($json) { return $handle.Invoke($null, @($json)) }

Write-Host ''
Write-Host '--- initialize ---'
$r = Rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"test","version":"1"}}}'
$o = $r | ConvertFrom-Json
Check "jsonrpc 필드가 2.0" ($o.jsonrpc -eq '2.0') $r
Check "id 가 그대로 반환됨" ($o.id -eq 1) $r
Check "protocolVersion 반향" ($o.result.protocolVersion -eq '2025-06-18') $r
Check "tools capability 선언" ($null -ne $o.result.capabilities.tools) $r
Check "serverInfo.name 존재" ($o.result.serverInfo.name -eq 'revit-mcp') $r
$fallback = (Rpc '{"jsonrpc":"2.0","id":101,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}') | ConvertFrom-Json
Check '미지원 프로토콜 버전을 그대로 반향하지 않음' ($fallback.result.protocolVersion -eq '2025-06-18') ''

Write-Host ''
Write-Host '--- notifications/initialized (알림은 응답 없음) ---'
$r = Rpc '{"jsonrpc":"2.0","method":"notifications/initialized"}'
Check "알림에는 null 을 돌려줌" ($null -eq $r) ("실제: " + $r)

Write-Host ''
Write-Host '--- ping ---'
$r = Rpc '{"jsonrpc":"2.0","id":2,"method":"ping"}'
$o = $r | ConvertFrom-Json
Check "ping 응답에 result 존재" ($null -ne $o.result) $r

Write-Host ''
Write-Host '--- tools/list ---'
$r = Rpc '{"jsonrpc":"2.0","id":3,"method":"tools/list"}'
$o = $r | ConvertFrom-Json
$tools = $o.result.tools
Check "tools 배열 반환" ($null -ne $tools) $r
Check ("도구 수가 " + $count + "개와 일치") ($tools.Count -eq $count) ("실제: " + $tools.Count)

$badName = @($tools | Where-Object { -not $_.name })
$badDesc = @($tools | Where-Object { -not $_.description })
$badSchema = @($tools | Where-Object { $_.inputSchema.type -ne 'object' })
Check "모든 도구에 name 존재" ($badName.Count -eq 0) ($badName.Count.ToString() + "개 누락")
Check "모든 도구에 description 존재" ($badDesc.Count -eq 0) ($badDesc.Count.ToString() + "개 누락")
Check "모든 inputSchema.type 이 object" ($badSchema.Count -eq 0) ($badSchema.Count.ToString() + "개 불량")

$dupes = @($tools | Group-Object name | Where-Object { $_.Count -gt 1 })
Check "도구 이름 중복 없음" ($dupes.Count -eq 0) (($dupes | ForEach-Object { $_.Name }) -join ',')

$writes = @('send_code_to_revit','update_energy_settings','create_energy_model','export_energy_gbxml','request_systems_analysis','cancel_systems_analysis','create_clash_review_views','apply_cad_layout')
foreach ($write in $writes) {
    $tool = $tools | Where-Object name -eq $write
    Check ($write + ' 문서·요청 보호 인자 필수') (($tool.inputSchema.required -contains 'requestId') -and ($tool.inputSchema.required -contains 'expectedDocument') -and ($tool.inputSchema.required -contains 'expectedRevision')) ''
}
$local = $tools | Where-Object name -eq 'get_request_status'
Check '상태 조회에 Revit 문서 토큰 불필요' ($local.inputSchema.required -notcontains 'expectedDocument') ''
$context = $tools | Where-Object name -eq 'get_document_context'
Check '최초 문맥 조회에 문서 토큰 불필요' ($context.inputSchema.required -notcontains 'expectedDocument') ''

Write-Host ''
Write-Host '--- 오류 처리 ---'
$r = Rpc '{"jsonrpc":"2.0","id":4,"method":"no/such/method"}'
$o = $r | ConvertFrom-Json
Check "없는 method 는 -32601" ($o.error.code -eq -32601) $r

$r = Rpc 'this is not json'
$o = $r | ConvertFrom-Json
Check "깨진 JSON 은 -32700" ($o.error.code -eq -32700) $r

# tools/call 은 UIApplication 이 걸린 대리자를 거치므로 RevitAPIUI 가 필요하다.
# RevitAPIUI 는 혼합 모드라 Revit 프로세스 밖에서는 로드되지 않는다. 여기서는 건너뛴다.
$r = Rpc '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}'
if ($r -match 'RevitAPIUI') {
    Write-Host '  [SKIP] tools/call 경로 - Revit 프로세스 밖에서는 확인 불가' -ForegroundColor Yellow
    Write-Host '         실제 호출 검증은 Revit 을 띄운 뒤 test-live.ps1 로 한다.' -ForegroundColor Yellow
} else {
    $o = $r | ConvertFrom-Json
    Check "없는 도구는 isError=true" ($o.result.isError -eq $true) $r
}

Write-Host ''
Write-Host '--- 도구 목록 ---'
$tools | Sort-Object name | ForEach-Object {
    $req = if ($_.inputSchema.required) { $_.inputSchema.required -join ',' } else { '-' }
    Write-Host ("  {0,-34} 필수: {1}" -f $_.name, $req)
}

Write-Host ''
if ($fail -eq 0) {
    Write-Host ("=== " + $checks + "개 전부 통과 ===") -ForegroundColor Green
    exit 0
} else {
    Write-Host ("=== 실패 " + $fail + "건 ===") -ForegroundColor Red
    exit 1
}
