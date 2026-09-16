# Revit MCP — Revit 2024 로컬 서버

> **2.0.0-preview.1 개발판:** 요청·문서·복구 보호와 해석·간섭·CAD 도구를 추가했습니다. 변경 요청 인자가 달라졌으며, 실제 Revit 모델 검증 전입니다. [안정성/호출 계약](docs/STABILITY.md)과 [검증 범위](docs/VALIDATION.md)를 먼저 확인하세요.

Revit 애드인 안에서 HTTP MCP 서버를 직접 실행하는 C# 프로젝트입니다. AI 클라이언트가 모델 요소를 조회·생성·수정하고, C# 코드로 Revit API를 호출할 수 있습니다. 별도의 Node.js 서버나 운영자 승인 서버는 사용하지 않습니다.

```text
MCP 클라이언트 → http://127.0.0.1:8090/mcp → RevitMcp.dll → Revit API
```

**Windows / Revit 2024용 소스와 설치 스크립트입니다.** HTTPS로 복제하거나 ZIP을 내려받고 `setup.ps1 -Install`을 실행하면 의존성 준비·빌드·검사·설치를 진행합니다. Revit 자체는 별도로 설치되어 있어야 합니다. 다른 PC의 Revit 실제 동작은 별도 검증 대상입니다. 상세 결과는 [검증 기록](docs/VALIDATION.md)을 확인하세요. Autodesk의 공식 제품이 아닙니다.

## 빠른 시작 — Code → HTTPS

Git이 설치되어 있다면 PowerShell에서 다음을 실행하세요. 먼저 Revit 작업을 저장하고 모든 Revit 창을 종료합니다.

```powershell
git clone --branch feature/revit-workflows-preview --single-branch https://github.com/catsony5-web/revit-mcp.git
cd revit-mcp
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup.ps1 -Install
```

Git이 없다면 **Code → Download ZIP**을 내려받아 압축을 풀고, 해당 폴더에서 마지막 명령을 실행하면 됩니다. Fork는 자신의 GitHub 계정에 저장소 사본을 만드는 기능이며 설치에 필수는 아닙니다.

위 명령은 이번 시험판 브랜치를 복제합니다. GitHub에서 ZIP을 받을 때도 `feature/revit-workflows-preview` 브랜치를 먼저 선택합니다. `main`에는 기존 공개판을 유지합니다.

설치 후 Revit을 실행하고 `부가 기능 > MCP`에서 서버를 확인한 뒤, AI 클라이언트의 HTTP MCP 주소에 **`http://127.0.0.1:8090/mcp`**를 등록하세요. 클라이언트 연결 예시는 [examples/mcp-http.json](examples/mcp-http.json)에 있습니다. 클라이언트의 개인 설정 파일을 자동으로 덮어쓰지는 않습니다.

첫 준비에는 `api.nuget.org`에 연결 가능한 인터넷이 필요합니다. Python, Node.js, Visual Studio, 별도 .NET SDK는 필요하지 않습니다. Revit 2024 설치본과 Windows PowerShell 5.1, Windows .NET Framework 컴파일러를 사용합니다.

`setup.ps1`만 실행하면 빌드·검사까지만 수행합니다. `-Install`일 때만 애드인 설치 폴더에 쓰며, Revit이 실행 중이면 설치를 중단합니다. 업그레이드는 기존 애드인을 이 저장소의 `backups/`에 보관하고 개인 설정·로컬 데이터를 유지합니다.

## 주요 기능

- 활성 뷰·선택 요소·패밀리 유형·물량·모델 통계 조회
- 레벨·그리드·룸·벽·선 기반 패밀리·바닥 등 요소 생성
- 요소의 파라미터·위치·회전·유형 변경, 삭제, 색상 지정
- 치수·룸 태그·벽 태그, 룸 데이터 내보내기
- 프로젝트·룸 정보 및 재사용 C# 코드 조각의 로컬 JSON 보관
- `send_code_to_revit`으로 C# 코드 실행
- 요청 중복 방지·대상 문서/수정번호 검사·대기 취소·결과 복구 조회
- [해석 도구 10개](docs/ANALYSIS.md): 에너지 설정, 해석모델, 공간 진단, gbXML, 시스템 해석 요청/취소/조회
- [간섭 검사와 3D 검토 뷰](docs/CLASH.md): 호스트/링크 솔리드 교차, 범위·누락 보고, 결과별 섹션박스·색상
- [CAD 폴더 초기 배치](docs/CAD.md): 층/분야 분류, 레벨/평면뷰 계획, DWG/DXF 일괄 링크

해석 탭 전체, DWG 내부 도면의 의미 해석·여러 층 자동 분리, CAD를 BIM 요소로 자동 변환하는 기능은 아직 구현하지 않았습니다. 배관·덕트 생성은 전용 생성 도구가 없으며 필요한 경우 C# API 코드로 별도 처리합니다.

실제 도구 이름·입력은 `tools/list`에 노출됩니다. 등록된 도구 수와 검사 범위는 검증 기록에 남깁니다. Revit 작업별 성공 여부는 대상 모델의 상태·패밀리·호스트·권한에 따라 달라집니다.

## 빌드

일반 사용자는 `setup.ps1`으로 자동 준비할 수 있습니다. 의존성을 직접 준비하려면 [빌드 의존성 안내](docs/BUILD.md)를 참고하세요.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RoslynDir 'C:\Dependencies\Roslyn'
powershell -NoProfile -ExecutionPolicy Bypass -File .\test-protocol.ps1
```

결과는 `artifacts/2024/`에 생성됩니다. **빌드 스크립트는 설치된 애드인을 변경하지 않습니다.** Revit API DLL 및 제3자 DLL은 저장소에 포함하지 않습니다.

## 설치

1. Revit 작업을 저장하고 모든 Revit 창을 종료합니다.
2. 같은 이름의 기존 애드인이 있다면 `RevitMcp` 폴더와 `RevitMcp.addin`을 별도 보관합니다.
3. `powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1`을 실행합니다. 설치 위치는 `%APPDATA%\Autodesk\Revit\Addins\2024\`입니다. 기존 `revitmcp.config.json`, `RevitMcpData`, `RevitMcpLogs`는 보존합니다. 복사 계획만 확인하려면 `-WhatIf`를 붙이세요.
4. Revit을 실행하고 `부가 기능 > MCP`에서 서버 상태를 확인합니다.
5. HTTP MCP를 지원하는 클라이언트에 `http://127.0.0.1:8090/mcp`를 등록합니다. JSON 구성 예시는 [examples/mcp-http.json](examples/mcp-http.json)이며, 실제 설정 형식은 클라이언트마다 다릅니다.

기본값은 포트 `8090`, 자동 시작 `true`, 호출 제한 60초, 최대 제한 600초입니다. 포트는 설치 폴더의 `revitmcp.config.json`에서 변경합니다. 같은 포트를 사용하는 서버·Revit 인스턴스가 있으면 충돌할 수 있습니다. 다른 MCP 애드인을 자동으로 제거하거나 비활성화하지 않습니다.

제거할 때는 Revit을 종료하고 이 프로젝트의 `RevitMcp.addin`과 `RevitMcp` 폴더를 옮기거나 삭제합니다. 필요한 로컬 데이터는 먼저 보관하세요.

## 코드 실행

```json
{
  "requestId": "code-inspect-001",
  "expectedDocument": "<get_document_context active.expectedDocument>",
  "expectedRevision": 0,
  "code": "return new { title = doc.Title };",
  "transactionMode": "none"
}
```

위 입력은 `send_code_to_revit`의 인자입니다. `doc`/`document`, `uidoc`, `uiapp`, `app`, `parameters`를 사용할 수 있습니다.

`expectedRevision`은 예시이며 실제 조회값을 사용합니다. 읽기 코드여도 임의 C# 도구에는 세 보호 인자가 필요합니다. 같은 요청을 재전송할 때는 ID와 인자를 유지하고, 불명확한 결과는 `get_request_status`로 조회합니다.

| transactionMode | 동작 |
|---|---|
| `auto` | 서버가 트랜잭션을 열고 실행. 코드에서 중첩 트랜잭션을 열지 않습니다. |
| `manual` | 코드가 트랜잭션을 직접 관리합니다. |
| `none` | 서버가 트랜잭션을 열지 않습니다. 읽기 전용 권한을 강제하는 모드는 아닙니다. |

전용 모델링 도구는 주로 mm를 입력으로 사용합니다. **직접 작성하는 C#은 Revit API 내부 단위 규칙을 따릅니다.** 사용자 코드는 OS·파일 및 Revit API 권한으로 실행되며 보안 샌드박스가 아닙니다. 신뢰하는 로컬 클라이언트와 코드만 연결하세요.

## 제한과 검증

- 서버는 IPv4 루프백에 바인딩하며 인증 기능은 없습니다. 포트 전달·공개 프록시를 통한 외부 공개용으로 설계되지 않았습니다.
- 단일 JSON 응답을 사용하며 SSE 스트림은 제공하지 않습니다. 모든 MCP 클라이언트·프로토콜 버전에 대한 적합성을 보장하지 않습니다.
- 대기 시간초과는 시작 전 작업만 취소합니다. 실행 중인 작업은 요청 ID로 결과를 조회합니다. 재시작 후 불확실한 요청을 자동 재실행하지 않습니다.
- Windows 정책이 서명되지 않은 DLL을 차단할 수 있습니다. 조직의 승인·코드 서명 절차를 따르세요.
- `test-protocol.ps1`은 별도 PowerShell 프로세스에서 생성된 DLL의 프로토콜 계층을 검사합니다. 열린 Revit 서버에는 접속하지 않습니다.
- `test-live.ps1 -AllowLiveTest`는 Revit 조회·C# 실행 및 로컬 저장소 쓰기를 수행합니다. 별도로 준비한 시험 모델에서만 실행하세요. 이번 공개 작업에서는 실행하지 않았습니다.

## 출처와 배포 범위

기존 로컬 `RevitMcp` 소스를 공개용으로 정리했습니다. `RevitPilot`과 병렬 worker 개발판은 별도 프로젝트이며 포함하지 않습니다. [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)과 일부 도구 이름·사용 목적이 겹치지만, 이 저장소의 서버는 Revit 애드인 내부에서 직접 HTTP 요청을 처리합니다.

업무 모델·실행 로그·프로젝트 데이터·개인 설정은 포함하지 않습니다. 출처 및 이용 조건은 [NOTICE.md](NOTICE.md)를 참고하세요.

[다른 Revit MCP와의 비교](docs/COMPARISON.md)에서는 서버 구조·설치·도구와 검증 범위를 구분합니다.
