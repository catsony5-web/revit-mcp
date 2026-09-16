# Revit MCP — Revit 2024 로컬 서버

Revit 애드인 안에서 HTTP MCP 서버를 직접 실행하는 C# 프로젝트입니다. AI 클라이언트가 모델 요소를 조회·생성·수정하고, C# 코드로 Revit API를 호출할 수 있습니다. 별도의 Node.js 서버나 운영자 승인 서버는 사용하지 않습니다.

```text
MCP 클라이언트 → http://127.0.0.1:8090/mcp → RevitMcp.dll → Revit API
```

**공개 상태: 개발자용 소스 배포.** Windows / Revit 2024 대상으로 작성되었습니다. 이번 공개 준비에서는 오프라인 빌드와 프로토콜 검사를 수행하며, 사용자 PC의 신규 설치와 실제 모델 동작은 별도 검증 대상입니다. 상세 결과는 [검증 기록](docs/VALIDATION.md)을 확인하세요. Autodesk의 공식 제품이 아닙니다.

## 주요 기능

- 활성 뷰·선택 요소·패밀리 유형·물량·모델 통계 조회
- 레벨·그리드·룸·벽·보·배관·바닥 등 요소 생성
- 요소의 파라미터·위치·회전·유형 변경, 삭제, 색상 지정
- 치수·룸 태그·벽 태그, 룸 데이터 내보내기
- 프로젝트·룸 정보 및 재사용 C# 코드 조각의 로컬 JSON 보관
- `send_code_to_revit`으로 C# 코드 실행

실제 도구 이름·입력은 `tools/list`에 노출됩니다. 등록된 도구 수와 검사 범위는 검증 기록에 남깁니다. Revit 작업별 성공 여부는 대상 모델의 상태·패밀리·호스트·권한에 따라 달라집니다.

## 빌드

Windows PowerShell 5.1, Revit 2024 설치본, Microsoft Roslyn 의존 DLL이 필요합니다. [빌드 의존성 안내](docs/BUILD.md)에 따라 DLL을 준비한 뒤 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RoslynDir 'C:\Dependencies\Roslyn'
powershell -NoProfile -ExecutionPolicy Bypass -File .\test-protocol.ps1
```

결과는 `artifacts/2024/`에 생성됩니다. **빌드 스크립트는 설치된 애드인을 변경하지 않습니다.** Revit API DLL 및 제3자 DLL은 저장소에 포함하지 않습니다.

## 설치

1. Revit 작업을 저장하고 모든 Revit 창을 종료합니다.
2. 같은 이름의 기존 애드인이 있다면 `RevitMcp` 폴더와 `RevitMcp.addin`을 별도 보관합니다.
3. `artifacts/2024/RevitMcp/`와 `artifacts/2024/RevitMcp.addin`을 `%APPDATA%\Autodesk\Revit\Addins\2024\`에 복사합니다. 업그레이드할 때 기존 `revitmcp.config.json`, `RevitMcpData`, `RevitMcpLogs`는 보존합니다.
4. Revit을 실행하고 `부가 기능 > MCP`에서 서버 상태를 확인합니다.
5. HTTP MCP를 지원하는 클라이언트에 `http://127.0.0.1:8090/mcp`를 등록합니다. JSON 구성 예시는 [examples/mcp-http.json](examples/mcp-http.json)이며, 실제 설정 형식은 클라이언트마다 다릅니다.

기본값은 포트 `8090`, 자동 시작 `true`, 호출 제한 60초, 최대 제한 600초입니다. 포트는 설치 폴더의 `revitmcp.config.json`에서 변경합니다. 같은 포트를 사용하는 서버·Revit 인스턴스가 있으면 충돌할 수 있습니다. 다른 MCP 애드인을 자동으로 제거하거나 비활성화하지 않습니다.

제거할 때는 Revit을 종료하고 이 프로젝트의 `RevitMcp.addin`과 `RevitMcp` 폴더를 옮기거나 삭제합니다. 필요한 로컬 데이터는 먼저 보관하세요.

## 코드 실행

```json
{
  "code": "return new { title = doc.Title };",
  "transactionMode": "none"
}
```

위 입력은 `send_code_to_revit`의 인자입니다. `doc`/`document`, `uidoc`, `uiapp`, `app`, `parameters`를 사용할 수 있습니다.

| transactionMode | 동작 |
|---|---|
| `auto` | 서버가 트랜잭션을 열고 실행. 코드에서 중첩 트랜잭션을 열지 않습니다. |
| `manual` | 코드가 트랜잭션을 직접 관리합니다. |
| `none` | 서버가 트랜잭션을 열지 않습니다. 읽기 전용 권한을 강제하는 모드는 아닙니다. |

전용 모델링 도구는 주로 mm를 입력으로 사용합니다. **직접 작성하는 C#은 Revit API 내부 단위 규칙을 따릅니다.** 사용자 코드는 OS·파일 및 Revit API 권한으로 실행되며 보안 샌드박스가 아닙니다. 신뢰하는 로컬 클라이언트와 코드만 연결하세요.

## 제한과 검증

- 서버는 IPv4 루프백에 바인딩하며 인증 기능은 없습니다. 포트 전달·공개 프록시를 통한 외부 공개용으로 설계되지 않았습니다.
- 단일 JSON 응답을 사용하며 SSE 스트림은 제공하지 않습니다. 모든 MCP 클라이언트·프로토콜 버전에 대한 적합성을 보장하지 않습니다.
- 타임아웃과 취소 알림이 이미 시작한 모델 작업의 중단을 보장하지 않습니다. 결과가 불분명하면 재실행 전에 모델 상태를 확인하세요.
- Windows 정책이 서명되지 않은 DLL을 차단할 수 있습니다. 조직의 승인·코드 서명 절차를 따르세요.
- `test-protocol.ps1`은 별도 PowerShell 프로세스에서 생성된 DLL의 프로토콜 계층을 검사합니다. 열린 Revit 서버에는 접속하지 않습니다.
- `test-live.ps1 -AllowLiveTest`는 Revit 조회·C# 실행 및 로컬 저장소 쓰기를 수행합니다. 별도로 준비한 시험 모델에서만 실행하세요. 이번 공개 작업에서는 실행하지 않았습니다.

## 출처와 배포 범위

기존 로컬 `RevitMcp` 소스를 공개용으로 정리했습니다. `RevitPilot`과 병렬 worker 개발판은 별도 프로젝트이며 포함하지 않습니다. [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)과 일부 도구 이름·사용 목적이 겹치지만, 이 저장소의 서버는 Revit 애드인 내부에서 직접 HTTP 요청을 처리합니다.

업무 모델·실행 로그·프로젝트 데이터·개인 설정은 포함하지 않습니다. 출처 및 이용 조건은 [NOTICE.md](NOTICE.md)를 참고하세요.
