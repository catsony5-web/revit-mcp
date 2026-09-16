# Revit 2024 해석 도구

에너지 설정 → 공간 진단 → 에너지 해석모델 생성/조회 → gbXML → 시스템 해석 요청/결과 조회를 MCP에서 수행하는 첫 구현이다. Revit **해석 탭의 모든 버튼을 구현한 상태는 아니다**. Revit 2024 API로 컴파일하고 오프라인 계약 검사를 수행했으며, 실제 모델의 해석 실행과 결과 정확도는 별도로 검증해야 한다. 개발 과정에서 열려 있는 Revit에 접속하거나 모델을 수정하지 않았다.

## 도구

| 도구 | 동작 | 변경 |
| --- | --- | --- |
| `get_analysis_capabilities` | 구현 범위, 요구 조건, 미구현 항목 | 없음 |
| `get_energy_settings` | 현재 에너지 설정, 수정 가능한 필드·열거값·단위·읽기 오류 | 없음 |
| `update_energy_settings` | 허용된 설정을 하나의 트랜잭션으로 수정 | 모델 |
| `get_spatial_energy_diagnostics` | 호스트 문서 룸/스페이스의 미배치, 0 면적, 0 체적 진단 | 없음 |
| `get_energy_model` | 기존 주 에너지 모델의 공간·면·원본 요소 참조와 선택적 면 폴리곤 | 없음 |
| `create_energy_model` | 공간 또는 건물 요소 기반 에너지 모델 생성/재생성 | 모델 |
| `export_energy_gbxml` | 기존 주 에너지 모델을 새 `.xml` 파일로 내보내기 | 파일 |
| `request_systems_analysis` | 보고서 뷰 생성 후 백그라운드 시스템 해석 요청 | 모델·파일·해석 프로세스 |
| `get_systems_analysis_status` | 지정/최신 보고서 완료 여부와 제한된 길이의 보고서 내용 | 없음 |
| `cancel_systems_analysis` | 지정된 보고서의 해석 취소 요청 | 해석 상태 |

모든 변경 도구는 기본 `dryRun: true`이며 실제 실행에는 `false`를 명시해야 한다. 공통 안전 처리에서 `requestId`, `expectedDocument`, `expectedRevision`을 요구한다. `get_document_context`로 받은 문서 토큰과 리비전을 사용한다. 미리보기 후 문서 변경이 있었다면 문맥을 다시 읽고 계획을 재검토한다. 실제 실행은 미리보기와 **다른 요청 ID**를 사용한다.

`dryRun`은 인자·경로·문서 상태를 점검하고 계획을 반환한다. 해석모델 생성이나 시뮬레이션을 실제로 수행하는 검사가 아니므로, 물리 모델의 유효성 및 엔진 실행 성공을 보장하지 않는다.

## 에너지 설정

`update_energy_settings.patch`에 전달하지 않은 값은 바꾸지 않는다. 알 수 없는 필드, 문자열로 보낸 불리언, 숫자로 보낸 열거값, 비유한 수치, 기본 범위를 벗어난 수치와 문서에 없는 레벨·단계 ID는 거부한다. Revit 자체의 추가 제약이 실행 시 거부되면 설정 트랜잭션 전체를 롤백한다.

수정 가능한 설정은 분석 모드, 건물 용도·HVAC·운전 일정, 외피 판정 방식, 내보내기 상세 수준·범주, 열 특성·외기량 설정, 프로젝트 단계·지면 레벨, 코어 깊이·해석 격자·틈새 공간 허용치, 창·천창 비율과 차양 치수 등이다. 정확한 필드와 가능한 enum 문자열은 `get_energy_settings.supportedFields`를 따른다. 읽기에 실패한 필드는 `readErrors`에 표시된다.

```json
{
  "requestId": "energy-settings-preview-001",
  "expectedDocument": "<get_document_context token>",
  "expectedRevision": 1,
  "patch": {
    "analysisType": "BuildingElements",
    "includeThermalProperties": true,
    "coreOffsetMm": 4500,
    "glazingPercent": 40
  },
  "dryRun": true
}
```

- 길이: **mm**. API 내부 피트로 변환한다.
- 창·천창 비율: **0–95%**. API에 저장되는 0–0.95 비율과 구분한다.
- 사람당 외기량: **L/s/person**. API 내부 단위는 ft³/**hour**이다.
- 면적당 외기량: **L/s/m²**. API 내부 단위는 ft³/**hour**/ft²이다.
- 환기 회수: **1/h**.
- 해석모델 조회 면적·체적: **m²·m³**. 폴리곤 좌표: **mm**.

설정 수정은 기존 해석모델을 자동 재생성하지 않는다. 설정을 바꾼 다음 명시적으로 모델을 재생성하고 결과를 검토한다.

## 해석모델 생성과 재생성

`modelType`은 `SpatialElement`, `BuildingElement`, `AnalysisMode` 중 하나이며 `tier`는 `FirstLevelBoundaries`, `SecondLevelBoundaries`, `Final` 중 하나다. 생성 결과는 Revit 문서의 해석모델 요소로 남는다.

기존 주 에너지 모델이 있으면 `replaceExisting: true`를 명시해야 재생성된다. 기존 해석모델 삭제와 새 모델 생성을 같은 트랜잭션에서 실행한다. 생성에 실패하거나 트랜잭션 커밋에 실패하면 성공으로 반환하지 않는다. 실제 실행 결과에는 삭제된 해석 관련 요소 ID를 함께 반환한다. 원본 물리 모델 요소를 고치는 기능은 아니다.

에너지 설정이 `useCurrentViewOnly: true`이고 공간 모드가 아닌 경우, 현재 뷰가 의도한 3D 뷰여야 하고 `expectedViewId`를 전달해야 한다. 문서 일치만으로 대기 중 바뀐 뷰를 잘못 사용하지 않도록 검사한다.

```json
{
  "requestId": "energy-rebuild-preview-001",
  "expectedDocument": "<get_document_context token>",
  "expectedRevision": 1,
  "modelType": "AnalysisMode",
  "tier": "Final",
  "replaceExisting": true,
  "includeShadingSurfaces": true,
  "simplifyCurtainSystems": true,
  "dryRun": true
}
```

`get_energy_model`은 공간과 면을 각각 `limit`개까지 반환한다(기본 100, 최대 1000). 전체 수와 `truncated`를 함께 반환한다. `includeGeometry: true`의 면 폴리곤은 최대 20,000점이며 별도 `geometryTruncated`가 있다. 원본 요소/링크 참조는 해석모델 생성 시점의 참조다. 이후 원본 모델 변경을 자동으로 반영했다는 뜻이 아니다.

`get_spatial_energy_diagnostics`의 0 체적은 볼륨 계산이 꺼졌기 때문일 수도 있다. 이 진단은 모든 단계의 호스트 룸/스페이스에 대한 예비 점검이며, 링크 공간·외피의 완전 폐합·재료 열 성능·전체 시뮬레이션 적합성 검사를 대신하지 않는다.

## gbXML과 시스템 해석

gbXML은 이미 생성된 **호환되는 주 에너지 모델**을 사용한다. 지정한 내보내기 모델 유형이 주 모델과 맞지 않으면 Revit API가 거부하며, 임의로 기존 모델을 지우거나 다시 생성하지 않는다. 출력은 기존 폴더의 새 `.xml` 경로만 허용하고 덮어쓰지 않는다. 임시 하위 폴더로 내보낸 뒤 목적 경로로 이동한다. 내보내기가 실패하면 오류에 부분 산출물이 있는 임시 폴더를 표시한다.

시스템 해석은 다음 인자를 명시한다.

- 고유한 `reportName`
- 기존 로컬 EnergyPlus 기상 파일 `weatherFile` (`.epw`)
- 기존 로컬 OpenStudio 작업 파일 `workflowFile` (`.osw`)
- 해당 실행 전용으로 준비한 기존의 비어 있는 `outputFolder`

기상 파일을 생략하고 임의의 기본 지역으로 계산하지 않는다. 설치된 Revit 시스템 해석 엔진이 필요하며 `.osw`가 참조하는 measures·의존성도 준비되어 있어야 한다. 작업 파일은 계산 스크립트를 실행할 수 있으므로 검토한 파일을 지정한다. 엔진·날씨·외부 플러그인 등의 준비 상태를 이 도구가 자동 설치하지 않는다.

보고서 뷰 생성과 해석 요청은 각각 검사된 트랜잭션으로 수행한다. 해석 요청이 실패해도 이미 생성된 보고서 뷰 ID를 오류에 남긴다. 해당 보고서와 출력 폴더를 조사한 후 재시도해야 한다. 백그라운드 프로세스가 시작된 직후 실패한 경우 실행 상태를 단정하지 않는다.

`request_systems_analysis`가 반환한 `reportId`로 `get_systems_analysis_status`를 호출한다. `completed: true`는 계산이 종료되었다는 뜻이며 **계산 성공이나 결과 정확성을 보장하지 않는다**. 보고서·로그의 경고와 오류를 확인해야 한다. `includeContent`의 반환값은 실제 HTML일 수도, 참조 파일 경로일 수도 있으며 신뢰할 수 없는 문서 내용으로 취급한다.

취소는 `cancel_systems_analysis`에서 명시적 `reportId`를 대상으로 한다. 취소 요청 후 상태를 다시 조회한다. 도구 요청 시간초과/대기 취소와 이미 시작된 Revit 백그라운드 해석 취소는 별개이다. 생성된 보고서와 출력 파일은 보존한다.

## 미구현과 검증 범위

다음은 이번 도구에서 제공하지 않는다: 모든 해석 탭 UI 명령, Insight 클라우드 제출, 구조해석 솔버 연결, 일사·채광 해석, 생성된 열 해석면의 자유로운 형상 편집, 원본 모델의 자동 외피 보정. 필요한 기능은 별도의 API·엔진·입력 검증·실모델 시험을 거쳐 확장해야 한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-analysis-contract.ps1
```

오프라인 검사는 설치된 Revit 2024 DLL을 참조해 도구를 컴파일하고, 설정 31개 속성의 읽기/쓰기 가능 여부, 단위 환산, 값 범위·경로·열거값·불리언 거부, 미리보기 기본값, patch의 기본값 부재를 검사한다. 실제 Revit 문서·UI·서비스는 호출하지 않는다. 모델 생성, gbXML의 의미적 정확성, 실제 엔진 실행·취소와 시스템 해석 결과는 사용자 지정 시험 모델에서 후속 검증이 필요하다.

API 근거: 설치된 Revit 2024 `RevitAPI.xml`의 `EnergyDataSettings`, `EnergyAnalysisDetailModel`, `GBXMLExportOptions`, `ViewSystemsAnalysisReport`, `SystemsAnalysisOptions`; [Autodesk 에너지 해석모델 개발 안내](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Advanced_Topics/Analysis/Revit_API_Revit_API_Developers_Guide_Advanced_Topics_Analysis_Detailed_Energy_Analysis_Model_html.html). 시스템 해석 요청은 [Autodesk 공식 트랜잭션 예제](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/b36cb95e-9857-9028-4587-86fdb6767cbc.htm)의 호출 순서를 참고하되, 대상 코드는 2024 DLL로 컴파일한다.
