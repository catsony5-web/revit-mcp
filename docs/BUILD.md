# 빌드 의존성

대상은 Windows / Revit 2024 / .NET Framework입니다. PowerShell 5.1과 Windows의 Framework64 C# 컴파일러를 사용합니다. 애드인 본체는 C# 5 문법으로 작성되며 사용자 코드 실행에는 Roslyn을 사용합니다.

`-RevitDir` 기본값은 `%ProgramFiles%\Autodesk\Revit 2024`입니다. 이 폴더의 `RevitAPI.dll`, `RevitAPIUI.dll`, `Newtonsoft.Json.dll`을 참조합니다. Autodesk DLL은 복사·재배포하지 않습니다.

`-RoslynDir`로 아래 파일이 모두 있는 폴더를 지정합니다. 이미 보유한 정상 설치본의 의존성 폴더 또는 Microsoft의 NuGet 패키지에서 준비한 파일을 사용할 수 있습니다. 파일은 일치하는 버전 조합으로 준비하세요.

| DLL | 원본 환경에서 확인한 제품 버전 |
|---|---|
| Microsoft.CodeAnalysis.dll | 4.800.23.55801 |
| Microsoft.CodeAnalysis.CSharp.dll | 4.800.23.55801 |
| System.Collections.Immutable.dll | 7.0.22.51805 |
| System.Reflection.Metadata.dll | 7.0.22.51805 |
| System.Runtime.CompilerServices.Unsafe.dll | 6.0.21.52210 |
| System.Memory.dll | 4.6.31308.01 |
| System.Buffers.dll | 4.6.28619.01 |
| System.Numerics.Vectors.dll | 4.6.26515.06 |
| System.Threading.Tasks.Extensions.dll | 4.6.28619.01 |
| System.Text.Encoding.CodePages.dll | 7.0.22.51805 |

Roslyn의 패키지는 `Microsoft.CodeAnalysis.CSharp` 4.8.0 계열입니다. 위 표는 DLL 제품 버전으로, NuGet 패키지 버전과 표기가 다를 수 있습니다. 저장소에는 의존 DLL을 포함하거나 자동 다운로드하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RoslynDir 'C:\Dependencies\Roslyn'
```

출력은 저장소 내부 `artifacts/2024/`로 제한됩니다. 소스 인코딩은 컴파일러의 한글 처리를 위해 UTF-8 BOM으로 정규화합니다. 빌드는 Revit을 시작하거나 기존 애드인을 설치·교체하지 않습니다.
