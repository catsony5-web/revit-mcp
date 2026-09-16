# 공개 준비 검증 — 2026-09-16

## 이번에 확인한 범위

| 검사 | 결과 |
|---|---|
| Revit 2024 API를 참조한 C# 소스 20개 컴파일 | 통과 |
| 공개용 PowerShell 스크립트 3개 구문 | 통과 |
| 프로토콜 검사 | 16개 통과 |
| 등록 도구 수 | 29개 |
| 도구 이름·설명·객체 스키마·중복 이름 검사 | 통과 |
| initialize·ping·initialized 알림·오류 응답 | 통과 |
| tools/call 실제 실행 | Revit 외부에서 RevitAPIUI 로드 제한으로 생략 |
| 신규 설치·서버 HTTP 접속·실제 모델 생성/수정 | 이번 작업에서 미실행 |

빌드 산출물은 저장소 내부 `artifacts/2024/`에 생성했고, 설치 폴더에는 쓰지 않았습니다. 프로토콜 검사는 별도 PowerShell 프로세스에서 DLL을 불러 실행했으며 열린 Revit이나 MCP 서버에 접속하지 않았습니다. 검사에서 생성된 로그와 DLL은 Git 추적에서 제외합니다.

공개 준비 중 런타임 C# 구현은 변경하지 않았습니다. 빌드 출력 위치·의존성 입력을 정리하고, 프로토콜 검사 대상을 로컬 빌드 산출물로 바꾸었습니다. 라이브 검사에는 명시적 `-AllowLiveTest` 옵션을 추가했습니다.

개인 홈 경로·프로젝트 이름·일반적인 토큰/키 패턴을 공개 대상 텍스트에서 검사했습니다. 업무 모델·로그·설정·로컬 데이터·제3자 DLL은 배포 범위에 포함하지 않았습니다. 이 검사는 전체 보안 감사나 모든 MCP 클라이언트 호환성 인증이 아닙니다.

## 재현

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RoslynDir 'C:\Dependencies\Roslyn'
powershell -NoProfile -ExecutionPolicy Bypass -File .\test-protocol.ps1
```

의존 DLL 준비는 [BUILD.md](BUILD.md)를 참고하세요. 실제 모델 도구 검증은 별도의 시험 모델을 준비한 뒤 수행해야 합니다.
