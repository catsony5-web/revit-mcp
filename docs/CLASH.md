# 간섭검토와 3D 검토뷰

`scan_clashes`는 실제 Revit 솔리드의 체적 겹침을 검사하고, `create_clash_review_views`는 검사 결과를 다시 확인한 뒤 3D 단면상자 뷰를 만든다. 기존 별도 간섭검토 애드인에서 사용한 문서·링크 식별, 직접 접속부 분리, 좌표 변환, 계산 오류 별도 보고, 검토뷰 카메라 원칙을 MCP에 적용했다. 이 도구 자체의 실제 Revit 실행 검증은 아직 하지 않았다.

## 검사 범위 지정

`sources`와 `targets`에는 각각 `elements` 또는 `categories` 중 하나만 지정한다. 요소 번호는 현재 문서에서 조회한 값을 사용한다. 아래 번호는 설명용이며 실제 모델 번호로 바꿔야 한다.

```json
{
  "sources": {
    "elements": [{ "elementId": 101 }]
  },
  "targets": {
    "categories": ["OST_Walls", "OST_Floors", "OST_StructuralFraming"],
    "includeHost": true,
    "linkInstanceIds": [202]
  },
  "minimumIntersectionMm3": 1,
  "maxElementsPerGroup": 200,
  "maxPairs": 10000,
  "maxBooleanOperations": 1000,
  "maxIssues": 100,
  "timeBudgetMs": 2000
}
```

특정 링크 내부 요소는 `{"elementId": 303, "linkInstanceId": 202}`로 지정한다. 링크는 **배치 인스턴스**별로 식별한다. 같은 링크 파일을 여러 번 배치해도 검사 대상과 이슈가 섞이지 않는다. `includeHost: false`이면 지정한 링크 범주만 수집한다. 모든 링크를 암묵적으로 포함하지 않는다. 중첩 링크는 직접 검사하지 않는다.

기본 공정은 현재 뷰의 공정이며, 필요하면 호스트 `phaseId`를 지정한다. 링크 요소는 Revit 링크의 공정 매핑으로 대응시킨다. 없는 공정 매핑·비주 설계옵션·미로드 링크·삭제된 요소는 미검증으로 보고한다. 공정상 존재하지 않는 요소는 별도의 공정 제외 항목이다. 범주 수집은 뷰 표시 여부에 관계없이 해당 문서·공정 범위에서 수행한다.

## 결과 해석

- `status: completed`, `coverageComplete: true`: 명시한 범위에서 설정된 검사가 끝났다는 뜻이다. 건물 전체 무간섭 또는 시공 적합 판정이 아니다.
- `status: partial`: 요소 한도·쌍 한도·계산 한도·시간 한도·계산 오류·미지원 요소 등으로 검사 범위가 미완료다. `notices`와 `stopReason`을 읽고 범위를 나누어 다시 검사한다. 자동 이어하기 커서는 없다.
- `issues`: 경계상자가 겹치고 실제 솔리드 교차 체적이 임계값을 넘은 쌍이다. 호스트 모델, 원본 모델, 요소 UniqueId, 링크 인스턴스 UniqueId와 호스트 좌표계의 겹침 영역(mm)을 제공한다.
- `largestSolidIntersectionMm3`: 가장 큰 **단일 솔리드 쌍** 교차 체적이다. 중복 솔리드 체적을 과대 산정하지 않도록 여러 교차 체적을 합산하지 않는다. 임계값도 각 솔리드 쌍에 적용한다.
- `directConnectionPairsExcluded`: 직접 커넥터 접속·부모/하위 부품·보온/호스트 관계를 일반 간섭에서 분리한 개수다. 이 쌍을 무간섭으로 인증하지 않는다.
- `unverifiedPairs`: Boolean 실패·솔리드 부재·형상 제한 등으로 판정하지 못한 쌍이다. 계산 실패는 `exactClearPairs`에 들어가지 않는다.

모든 경계상자의 8개 모서리를 링크 변환까지 적용해 호스트 좌표계에 맞춘다. 실제 검사는 변환된 솔리드로 수행한다. 요소별 솔리드를 한 요청 안에서 재사용하고, 범주 필터와 경계상자로 불필요한 Boolean 계산을 줄인다. 기본 시간 한도는 2초, 상한은 5초이며 요소 수는 그룹당 최대 1,000개, 경계상자 비교는 최대 50,000쌍, Boolean 계산은 최대 5,000회다. 한 요소의 솔리드는 최대 100개까지 지원한다. **진행 중인 단일 Revit 네이티브 형상 연산을 강제로 중단하지는 못한다.** 따라서 이 시간은 UI 응답시간의 절대 보증이 아니다.

물리적 겹침만 검사한다. 필요한 이격거리·유지관리 공간·개구부의 적정성·정상 관통의 설계 판정·메시와 선만 있는 CAD의 정밀 충돌·중첩 링크·전체 설계옵션 비교는 지원 범위가 아니다.

## 3D 뷰 생성

`scanId`와 이슈의 `issueId`를 그대로 전달한다. 최근 최대 8개 검사만 세션 메모리에 보관하며 30분 뒤 만료된다. 애드인을 다시 시작하면 재검사가 필요하다. 문서를 닫고 다시 연 세션은 이전 결과를 사용할 수 없다.

```json
{
  "requestId": "clash-review-preview-unique-id",
  "expectedDocument": "get_document_context의 active.expectedDocument",
  "expectedRevision": 1,
  "scanId": "scan_clashes가 반환한 scanId",
  "issueIds": ["scan_clashes가 반환한 issueId"],
  "mode": "perIssue",
  "namePrefix": "MCP Clash",
  "paddingMm": 500,
  "dryRun": true
}
```

`dryRun`은 기본값이 `true`다. 선택한 최대 20개 이슈의 **현재** 요소·링크 배치·공정·솔리드를 다시 검사하고 예정 이름과 단면상자를 보여준다. `mode: grouped`는 선택 이슈를 하나의 3D 뷰로 묶는다. **미리보기와 적용 모두** 공통 문서/요청 보호 필드인 `requestId`, `expectedDocument`, `expectedRevision`이 필요하다. 문서 보호값은 `get_document_context`에서 받은 현재 값으로 바꾼다. 적용은 미리보기와 다른 고유 `requestId`와 `dryRun: false`를 사용한다. 같은 실행의 재시도는 원래 요청 ID와 원래 인자를 유지한다.

하나라도 재검증에 실패하면 `status: notApplied`와 `skipped`를 반환하며 뷰를 만들지 않는다. 최신 겹침 영역에 여백을 더한 단면상자, 정규 직교 카메라, 호스트 A/B 색상, 검토 설명을 생성한다. 기존 사용자 뷰는 덮어쓰지 않고 같은 이름이 있으면 숫자 접미사를 붙인다. 트랜잭션이 실패하면 이번 뷰 생성 전체를 롤백한다. 호스트 대상과 링크 인스턴스가 생성 뷰에 포함되는지 검사한다.

**링크 내부 개별 요소의 색상 제어와 개별 표시 여부는 보증하지 않는다.** 링크 대상은 단면상자 안의 링크 인스턴스 전체에 색상을 적용하며 이를 결과에 명시한다. 모델에 대체 검토 솔리드/가짜 물량을 생성하지 않는다. 3D 뷰는 저장되기 전 현재 문서 변경이며, 도구는 저장·동기화·현재 활성뷰 전환을 수행하지 않는다.

## 검증 수준

`tests/test-clash-support.ps1`은 Revit을 실행하지 않고 경계상자 판정·합집합·여백·링크별 이슈 식별·이름 충돌 방지를 확인한다. 전체 애드인은 Revit 2024 SDK 어셈블리로 빌드한다. 실제 Boolean 안정성, 실제 링크 변환 사례, 템플릿/공정/작업세트에 따른 뷰 표시와 성능은 별도 복사 모델에서 실행 확인이 필요하다. 과거 별도 애드인의 성공을 이 새 MCP 도구의 실행 검증으로 표시하지 않는다.
