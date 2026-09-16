# 검증 기록 — 2026-09-16

## 2.0.0-preview.1 오프라인 개발 검증

28개 C# 소스를 설치된 **Revit 2024 API**에 참조 컴파일했고 **47개 도구**의 등록을 확인했다. 기존 공개판과 달리 이번 버전은 런타임 요청 처리와 기능 구현을 변경했다. 열린 Revit·업무 모델·실행 중 MCP 서버에 접속하거나 애드인을 교체하지 않았다.

| 검사 | 결과 / 실제 범위 |
|---|---|
| 전체 참조 컴파일 | 통과, C# 소스 28개 |
| MCP 프로토콜 | 27개 통과; 47개 도구 스키마, 변경 보호 필수 인자, 프로토콜 협상 |
| 요청 수명·중복·복구 | 35개 통과; 취소, 재시작 기록, 독립 큐의 동시 기록 선점, 기록 오류 |
| 문서 보호 정책 | 40개 통과; 실제 보호 코드 + 가짜 Revit 경계 타입 |
| 서버 중지/재시작·설정 | 24개 통과; 독립 localhost 시험 서버, 실제 큐/서버 코드 + 가짜 Revit 경계 |
| 에너지 설정/API 계약 | 87개 통과; 2024 속성 31개, 단위/값/경로 검증 |
| 간섭 보조 로직 | 23개 통과; 경계상자/이슈 식별/뷰 이름 |
| CAD 파일·계획 | 58개 통과; 층 범위·중복·폴더 제한·SHA256·계획 ID 검증 |
| 설치 모의 검사 | 10개 통과; 별도 임시 폴더만 사용 |
| 로컬 JSON 저장소 | 25개 통과; 프로세스 내 동시 쓰기·백업·손상/빈 파일 보존 |
| PowerShell 구문 | 14개 스크립트 파싱 통과 |

위 329개 검사는 실모델 성공률이나 성능 점수가 아니다. 특히 가짜 Revit 경계 타입을 사용한 검사는 실제 Revit 이벤트 전달·네이티브 객체·UI 스케줄링을 재현하지 않는다. 에너지 검사 시 기존 공통 `ElementId(int)` 생성자에 대한 Revit 2024 사용 중단 예정 경고가 있으나 컴파일 오류는 없다.

**미검증:** Revit 안의 새 HTTP 도구 호출, 실제 중지/문서 전환 경합, 에너지 모델 생성·gbXML 내용·외부 해석 엔진, 간섭 Boolean/링크/공정/3D 표시, CAD DWG/DXF 링크·축척·진북·좌표 및 실패 롤백. 여러 PC·Revit 버전, 다른 MCP와의 속도 비교도 수행하지 않았다. 기본 물량 도구의 정확도 개선은 이번 범위에 포함하지 않았다.

이번 단계는 소스 시험판이다. 해석 탭 전체 지원이나 DWG 내부의 층별 의미 인식은 구현하지 않았다. 제약과 실행 계약은 [STABILITY.md](STABILITY.md), [ANALYSIS.md](ANALYSIS.md), [CLASH.md](CLASH.md), [CAD.md](CAD.md)를 따른다.

### 재현 명령

`setup.ps1`은 설치 없이 의존성·빌드·프로토콜만 검사한다. 다음 명령은 나머지 오프라인 검사를 각각 실행한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-request-queue.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-document-guard.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-server-lifecycle.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-analysis-contract.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-clash-support.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-cad-planning.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-data-store.ps1
```

## 아래는 기존 1.x 공개 준비 당시의 기록

아래 20개 소스·29개 도구·16개 검사·깨끗한 HTTPS 복제 결과는 **과거 공개판 기준**이며 이번 2.0 시험판의 결과로 대체 해석하지 않는다.

## 이번에 확인한 범위

| 검사 | 결과 |
|---|---|
| Revit 2024 API를 참조한 C# 소스 20개 컴파일 | 통과 |
| 공개용 PowerShell 스크립트 7개 구문 | 통과 |
| 공식 NuGet 의존 패키지 10개 및 SHA-256 검증 | 통과 |
| setup.ps1 자동 의존성 준비·빌드·검사 | 통과 (`-Install` 없이 실행) |
| 공개 GitHub HTTPS 신규 복제 후 setup.ps1 | 통과 (기존 캐시 없이 패키지 10개 다운로드, 빌드, 16개 프로토콜 검사) |
| 설치 스크립트 파일시스템 모의 검사 | 10개 통과 (별도 가짜 애드인 폴더) |
| 프로토콜 검사 | 16개 통과 |
| 등록 도구 수 | 29개 |
| 도구 이름·설명·객체 스키마·중복 이름 검사 | 통과 |
| initialize·ping·initialized 알림·오류 응답 | 통과 |
| tools/call 실제 실행 | Revit 외부에서 RevitAPIUI 로드 제한으로 생략 |
| 신규 설치·서버 HTTP 접속·실제 모델 생성/수정 | 이번 작업에서 미실행 |

빌드 산출물은 저장소 내부 `artifacts/2024/`에 생성했고, 설치 폴더에는 쓰지 않았습니다. 프로토콜 검사는 별도 PowerShell 프로세스에서 DLL을 불러 실행했으며 열린 Revit이나 MCP 서버에 접속하지 않았습니다. 검사에서 생성된 로그와 DLL은 Git 추적에서 제외합니다.

공개 준비 중 런타임 C# 구현은 변경하지 않았습니다. 빌드 출력 위치·의존성 입력을 정리하고, 프로토콜 검사 대상을 로컬 빌드 산출물로 바꾸었습니다. 라이브 검사에는 명시적 `-AllowLiveTest` 옵션을 추가했습니다. 자동 의존성 준비와 설치 스크립트를 추가해 기존 다른 MCP 설치가 없어도 소스를 빌드할 수 있게 했습니다.

설치 모의 검사는 `artifacts/tests/`에 가짜 DLL·설정·대상 폴더를 만들고 수행했습니다. WhatIf 무변경, 실행 중인 Revit 조건에서 설치 거절, 신규 DLL·매니페스트 복사, 업그레이드, 설정·데이터·다른 애드인 보존, 백업 생성·이전 DLL 보존을 확인했습니다. 실제 설치 폴더는 변경하지 않았습니다.

GitHub에 게시한 `9a27891` 커밋을 HTTPS로 새 폴더에 복제한 뒤, 기존 의존성 캐시나 설치된 MCP DLL에 의존하지 않고 `setup.ps1`을 실행해 빌드·프로토콜 검사를 재현했습니다. 이는 이 PC에서의 깨끗한 소스 복제 검사이며, 다른 PC의 설치 및 Revit 실모델 동작 검증을 뜻하지 않습니다.

개인 홈 경로·프로젝트 이름·일반적인 토큰/키 패턴을 공개 대상 텍스트에서 검사했습니다. 업무 모델·로그·설정·로컬 데이터·제3자 DLL은 배포 범위에 포함하지 않았습니다. 이 검사는 전체 보안 감사나 모든 MCP 클라이언트 호환성 인증이 아닙니다.

## 재현

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-install.ps1
```

의존 DLL 준비는 [BUILD.md](BUILD.md)를 참고하세요. 실제 모델 도구 검증은 별도의 시험 모델을 준비한 뒤 수행해야 합니다.
