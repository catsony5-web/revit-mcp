# 다른 Revit MCP와의 차이

확인 기준: 2026-09-16. 이 저장소 소스와 상대 프로젝트의 공개 README를 비교했습니다. 상대 구현 전체를 실행하거나 성능 비교를 한 결과는 아닙니다.

| 구분 | 이 저장소의 RevitMcp | 링크의 revit-mcp |
|---|---|---|
| 서버 위치 | C# 애드인 내부에 HTTP MCP 서버 | 별도 TypeScript/Node.js MCP 서버 |
| 연결 | 클라이언트 → HTTP → Revit 애드인 | 클라이언트 → Node.js 서버 → Revit 플러그인 |
| 설치 준비 | Revit 2024, PowerShell; 의존 DLL 자동 준비·빌드 | Node.js 18+, 서버 npm 빌드, 별도 플러그인 준비 |
| 주된 기능 | 요소 조회·생성·수정, 치수·태그, C# 실행 | 요소 조회·생성·수정, 태그, C# 실행 등 |
| 로컬 구현 특징 | 29개 도구 등록; modify_element와 코드 조각 search_modules/use_module 구현 | 해당 README 도구 목록에는 이 세 도구가 기재되어 있지 않음 |
| 데이터 보관 | 애드인 폴더의 JSON 파일 | 구체적인 비교는 상대 구현별 확인 필요 |
| 이번 검증 | 컴파일·오프라인 프로토콜·모의 설치 검사 | 이번 작업에서 실행하지 않음 |

비교 대상: https://github.com/mcp-servers-for-revit/revit-mcp

해당 링크는 2026-02-25 보관 처리되었고, [후속 통합 저장소](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)를 안내합니다. 후속판은 서버·플러그인·명령 구현을 함께 관리하고 npm 및 버전별 배포 설치를 안내합니다. 예전 저장소의 제약을 후속판 전체의 제약으로 해석하면 안 됩니다.

이 프로젝트의 확인 가능한 장점은 별도 Node.js 서버 없이 애드인 내부에서 직접 HTTP MCP를 제공하는 구성과 한국어 설치·운영 안내입니다. 더 빠르다거나 모든 도구가 더 안정적이라는 비교 성능 주장은 하지 않습니다. Revit 2024 외 버전, 여러 Revit 프로세스의 자동 선택, 모든 MCP 클라이언트 호환성은 검증하지 않았습니다.
