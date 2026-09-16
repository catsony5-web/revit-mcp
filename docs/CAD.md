# 폴더 기반 CAD 초기 배치

`inspect_cad_folder → plan_cad_layout → apply_cad_layout`로 여러 도면을 층별로 링크할 수 있습니다. 건축·구조·설비 등 하위 폴더를 함께 읽고, 파일별 링크·레벨·평면뷰 생성을 한 배치로 처리합니다. 도면 원본을 수정하거나 Revit을 저장·동기화하지 않습니다.

현재 구현은 **파일·폴더 이름을 해석한 배치 준비 기능**입니다. DWG 형상·문자·표제란·블록·외부참조를 읽어 도면을 이해하는 CAD 엔진은 포함하지 않습니다. DWG 헤더 6바이트로 버전 문자열만 확인합니다. 파일명이 충분하지 않으면 명시적인 층·단위·기준점 매핑을 받아야 합니다.

## 사용 순서

1. `inspect_cad_folder`에 로컬 도면 폴더를 전달합니다.
2. 파일 목록의 `floorCandidates`, `drawingKind`, `discipline`, `duplicateCandidates`와 `issues`를 확인합니다.
3. `plan_cad_layout`으로 기존 레벨·평면뷰와 연결하고 미해결 항목을 해소합니다. 단위와 정렬 기준을 반드시 지정합니다.
4. 전체 계획을 검토하고 `get_document_context`로 현재 문서·수정번호를 읽습니다. `apply_cad_layout`의 `dryRun: true`에도 `requestId`, `expectedDocument`, `expectedRevision` 세 필드를 모두 전달하여 파일 변경, 문서·레벨·뷰 변경, 기존 링크를 검사합니다.
5. 실제 적용은 새 `requestId`와 최신 `expectedDocument`·`expectedRevision`으로 `dryRun: false`를 호출합니다. 미리보기와 실제 적용은 서로 다른 요청이며 같은 요청 ID를 공유하지 않습니다. 동일 요청의 재시도는 원래 ID를 유지합니다. 실행에 필요한 공통 인자는 [안정성 문서](STABILITY.md)를 참고하십시오.

```json
{
  "root": "C:\\CAD\\Project",
  "maxFiles": 500,
  "maxDepth": 8
}
```

`root`는 Revit을 실행하는 컴퓨터의 폴더입니다. 파일 업로드·ZIP 해제 기능은 포함하지 않습니다. UNC, 네트워크 드라이브, 정션·심볼릭 링크를 따라가지 않으며, 접근 불가 항목도 결과에 표시합니다. 기본 최대 500개, 상한 2,000개 CAD 파일·20,000개 전체 항목·최대 깊이 16으로 제한합니다. 계획은 깊이 12로 검사합니다. 불완전한 검색은 적용을 차단하므로 폴더를 좁혀 다시 계획하십시오.

## 층별 도면 매핑 예

다음은 실제 프로젝트 정보가 아닌 예시입니다. 좌표는 모두 mm이며 **Revit 내부 원점 좌표계**를 기준으로 합니다. `sourceAnchorMm`는 선택한 CAD 단위가 mm로 변환된 뒤의 CAD 기준점입니다. `targetAnchorMm.z`는 대상 레벨의 표고와 같아야 합니다.

```json
{
  "root": "C:\\CAD\\Project",
  "onlyMappedFiles": true,
  "unit": "millimeter",
  "createMissingLevels": true,
  "createMissingViews": true,
  "thisViewOnly": true,
  "mappings": [
    {
      "relativePath": "Architecture\\01F Floor Plan.dwg",
      "floor": "1F",
      "elevationMm": 0,
      "viewName": "CAD Architecture 1F",
      "sourceAnchorMm": {"x": 100000, "y": 200000, "z": 0},
      "targetAnchorMm": {"x": 0, "y": 0, "z": 0},
      "rotationDegrees": 0
    },
    {
      "relativePath": "Structure\\02F Floor Plan.dwg",
      "floor": "2F",
      "elevationMm": 4200,
      "viewName": "CAD Structure 2F",
      "sourceAnchorMm": {"x": 100000, "y": 200000, "z": 0},
      "targetAnchorMm": {"x": 0, "y": 0, "z": 4200},
      "rotationDegrees": 0
    }
  ]
}
```

기존 레벨과 뷰가 여러 개면 `levelId`·`viewId`로 지정합니다. 새 레벨은 `createMissingLevels: true`와 파일 매핑의 `floor`·`elevationMm`가 모두 있어야 만들 수 있습니다. 새 뷰도 `createMissingViews: true`일 때만 만듭니다. 구조·설비 도면용 뷰도 현재는 일반 바닥 평면뷰를 생성합니다. 뷰 템플릿·구조/기계 분야의 표현 설정은 자동 지정하지 않습니다.

모든 도면이 동일한 XY 원점·방향을 사용함을 확인했다면 `confirmSharedOrigin: true`로 기본 기준점을 사용할 수 있습니다. 그때 CAD `(0,0,0)`을 Revit `(0,0,해당 레벨 표고)`에 맞춥니다. 공유좌표 취득이나 북쪽 방향을 자동 추정하는 옵션은 아닙니다. 회전은 XY 평면에서 반시계방향 각도입니다. 단위는 `millimeter`, `centimeter`, `meter`, `inch`, `foot` 중 하나를 명시합니다.

`thisViewOnly: true`가 기본값이며 해당 평면뷰에만 보이는 밑그림으로 링크합니다. `false`는 레벨에 연결된 모델 공간 링크를 만듭니다. 같은 파일·대상에 재적용하면 추적 정보와 기준점·회전을 확인해 `alreadyPresent`로 반환합니다. 이전 링크의 파일이나 설정이 바뀌었거나 사람이 이동·회전했으면 자동으로 덮어쓰지 않고 검토를 요구합니다. 추적 정보가 없는 기존 링크도 같은 경로·뷰 또는 레벨에서 발견하면 중복 배치를 차단합니다.

적용은 공통 오류 처리기를 사용하는 하나의 Revit 트랜잭션으로 처리하며 어느 파일에서든 오류가 나면 전체 배치를 되돌립니다. 적용 후 도면을 고정하고 기준점 위치를 1 mm 허용오차로 검사합니다. 선택한 파일은 계획 시 SHA-256으로 확인하고 적용 전과 링크 직후에도 대조합니다. 파일당 256 MiB, 배치 합계 1 GiB, 100개 배치 상한이 있으며 큰 도면은 분리하거나 정리해야 합니다. 폴더 검사는 모든 CAD를 해시하지 않고 이름·크기·수정시각·DWG 헤더만 읽습니다. 계획 후 적용이 끝날 때까지 원본 파일을 변경하지 마십시오. `dryRun`은 선행 조건 검사이며 실제 CAD 변환 성공을 보장하지 않습니다. Revit API가 파일을 거절하면 임의로 Import 방식으로 전환하지 않습니다.

적용할 계획의 `createLevel`/`createView`는 boolean, `levelId`/`viewId`는 정수여야 합니다. 생성 플래그가 true이면 ID는 0이어야 하며, false이면 양의 기존 ID여야 합니다. 실제 적용 시 문서의 이름·표고·뷰 연결도 다시 검사합니다. 좌표 변환과 재시도 검증에는 진북 변환이 포함되는 `GetTotalTransform()`을 사용합니다. 진북이 회전된 실제 프로젝트에서의 정렬은 아직 별도 실모델 검증이 필요합니다.

## 자동 판단하지 않는 항목

- `지하1층`, `B1`, `1F`, `1층`, `RF` 등 명시적인 층 표현을 읽습니다. 도면번호·계단번호·상세번호를 층으로 간주하지 않습니다. 파일명이나 상위 폴더의 `2-5층`, `2–5F`, `2 및 5층`, `2층부터/까지` 같은 범위 표현은 마지막 층 하나로 추정하지 않고 다층 여부를 확인하도록 차단합니다.
- 상세·확대·입면·단면 도면은 자동 배치 대상에서 제외하고 미해결로 표시합니다. 의도적으로 배치하려면 해당 파일 매핑에 `allowNonPlan: true`를 지정합니다.
- `_recover`, 백업·원본 폴더, 원본/복구본 쌍은 선택 확인이 필요합니다. `include: false` 또는 `onlyMappedFiles: true`로 제외할 수 있습니다.
- 여러 층이 한 DWG 모델 공간에 나열되어 있거나 도면이 종이 공간에만 있으면 자동 분리하지 않습니다. 층별 모델 공간 DWG로 나누거나 향후 CAD 영역 추출 연동이 필요합니다. `singleFloorConfirmed`는 이름만 여러 층으로 보이나 실제로 단일 층임을 사람이 확인한 경우에만 사용합니다.
- 같은 층 도면이라도 건축·설비·구조의 원점·단위·회전이 다르면 각각 기준점을 제공해야 합니다. 파일 이름은 정렬 정확성의 증거가 아닙니다.
- 배치된 CAD에서 벽·기둥·배관 등을 BIM 요소로 변환하는 기능은 이 도구의 범위가 아닙니다.

## 검증 범위

`tests/test-cad-planning.ps1`은 Revit을 로드하거나 연결하지 않고 C# 5로 순수 계획 코드를 컴파일하여 한국어/영문 층 해석, 중복·다층·상세도 처리, 로컬 경로 제한, 검색 상한, 파일 변경 감지를 검사합니다. Revit 2024 DLL을 참조한 컴파일은 API 서명을 검사합니다. 실제 DWG/DXF 링크, 좌표·스케일, 실패 복구, 3D/평면 표시의 실모델 검증은 별도 필요합니다.

API 근거: Autodesk의 [DWG/DXF 링크 API](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f3112a35-91c2-7783-f346-8f21d7cb99b5.htm)와 [DWGImportOptions](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/fcba2c30-7e6d-9ab7-8378-f4c6d5de06bf.htm). 링크 문서는 2026 API 설명이며, 이 구현의 지원 대상과 실제 컴파일 참조는 Revit 2024입니다.
