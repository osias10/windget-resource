# Resource Status

Windows 11 x64용 CPU·RAM·GPU·네트워크 상태 위젯입니다. 작은 위젯은 `ai_sa`의 간결한 행 배치를 참고하고, 큰 위젯은 최근 60초 추이를 함께 표시합니다.

## 실행과 사용

[GitHub 릴리즈](https://github.com/osias10/windget-resource/releases/latest)에서 `ResourceStatus-0.0.2-win-x64-setup.exe`를 받아 설치합니다. 현재 사용자용으로 설치되며 시작 메뉴에서 **Resource Status**를 실행할 수 있습니다. Windows 11 x64 빌드 22621 이상을 대상으로 하며 관리자 권한이나 별도의 .NET 설치는 필요하지 않습니다.

설치 없이 사용하려면 `ResourceStatus-0.0.2-win-x64.zip`을 빈 폴더에 모두 압축 해제한 뒤 `ResourceStatus.exe`를 실행합니다. EXE와 함께 배포된 DLL 파일도 같은 폴더에 보관하세요. 설치 파일을 직접 실행하거나 앱을 제거할 때는 트레이 메뉴의 **종료**로 위젯을 완전히 종료하세요. 앱 내 업데이트는 종료를 자동으로 처리합니다. 제거해도 사용자 설정은 보존됩니다.

`v0.0.1`에는 업데이트 메뉴가 없으므로 이번 `v0.0.2` 설치 파일로 한 번 업데이트해야 합니다. 이후에는 **설정 → 일반 → 업데이트 확인**, 위젯 우클릭 또는 트레이의 **업데이트 확인**을 사용합니다. 새 버전이 있으면 **다운로드 → 설치하고 다시 시작**을 누르세요. 설치된 앱은 기존 경로에 업데이트하며, 포터블 버전은 설치 마법사가 열립니다. 설정과 위치는 유지됩니다. 업데이트 확인과 설치는 사용자가 버튼을 누를 때 실행됩니다.

업데이트 파일은 이 저장소의 정식 GitHub 릴리즈에서 받으며 파일 크기와 SHA-256 체크섬을 검사합니다. 취소하거나 검증에 실패한 다운로드는 실행하지 않습니다. 다운로드 경로는 `%LocalAppData%\ResourceStatus\Updates`입니다.

- 위젯을 드래그하여 이동합니다. 작은 위젯은 더블클릭하거나 우클릭 메뉴의 **설정**으로 표시 구성을 엽니다.
- 우클릭 또는 트레이 메뉴의 **위젯 크기**에서 작게/크게를 전환합니다. 큰 위젯 상단에도 작은 크기 전환·고정·설정·숨김 버튼이 있습니다.
- 설정의 **작은 위젯 / 큰 위젯** 탭에서 CPU·RAM·GPU·네트워크를 각각 켜거나 끄고, 숫자·게이지·그래프·이름·상세 용량을 선택합니다. 두 크기의 구성을 독립적으로 저장합니다.
- 항목을 숨겨도 선택한 표시 요소는 보존됩니다. 표시 중인 항목에는 요소를 하나 이상 남겨야 합니다. 모든 항목을 숨기면 설정을 다시 여는 버튼을 표시합니다.
- 변경 사항은 즉시 적용되고 자동 저장됩니다. **이 모드 기본값 복원**은 선택한 탭에만 적용됩니다. GPU와 네트워크 장치는 **일반** 탭에서 선택하며 두 모드가 공유합니다.
- **일반 → 테마**에서 **검은 테마 / 하얀 테마 / Windows 시스템**을 선택합니다. 작은·큰 위젯과 설정 창에 즉시 적용되고 재실행해도 유지됩니다. Windows 시스템은 Windows의 **개인 설정 → 색 → 기본 앱 모드**를 따르며, Windows 색 설정을 바꿔도 자동 반영합니다. 기존 설정의 기본 테마는 검은 테마입니다.
- **일반 → 반투명 배경 → 배경 투명도**에서 0~100%를 조절합니다. 기본값은 25%이고, 값이 높을수록 배경이 투명해집니다. 글자·그래프·버튼은 선명하게 유지되며 두 위젯에 동일하게 적용됩니다. 반투명 배경을 끄면 불투명하게 표시하고 선택했던 투명도는 보존합니다. 변경은 즉시 보이고 조작을 멈추면 자동 저장됩니다.
- Esc와 닫기는 트레이 숨김입니다. 완전히 종료하려면 트레이 메뉴의 **종료**를 사용합니다. 숨긴 동안 수집을 중단합니다.

처음에는 작은 모드·항상 위 표시·반투명 배경이 켜지고 Windows 로그인 시 자동 시작은 꺼집니다. 작은 모드의 기본 폭은 240 DIP, 큰 모드는 320 DIP이며 화면 배율을 반영해 내용에 맞춰 높이를 조절합니다. 내용이 모니터 작업 영역보다 길 때만 내부 스크롤을 제공합니다.

배경 투명도는 원격 데스크톱에서도 적용됩니다. 100%에서도 이동과 우클릭을 사용할 수 있도록 최소한의 배경 감지 영역을 유지합니다. Windows 고대비 모드에서는 가독성을 위해 불투명하게 표시하며 선택한 투명도는 변경하지 않습니다.

설정과 모드별 위치는 `%LocalAppData%\ResourceStatus\settings.json`에 저장됩니다. 손상된 설정은 같은 폴더에 `.corrupt-…json`으로 보관한 뒤 기본값으로 읽습니다. 오류 기록은 해당 폴더의 `error.log`에서 확인할 수 있습니다.

## 지표 읽기

1초 간격으로 수집하며 그래프는 최근 60초를 표시합니다. CPU·RAM·GPU의 그래프 축은 0~100%, 네트워크는 자동 축입니다. 측정 실패나 절전·숨김 동안의 공백을 0으로 채우지 않습니다.

- GPU 사용률은 선택한 GPU에서 가장 바쁜 엔진의 값입니다. 전용 메모리와 공유 메모리를 구분합니다. 하드웨어·드라이버에서 제공하지 않는 카운터는 **지원 안 됨**으로 표시할 수 있습니다.
- RAM·GPU 용량은 GiB 등 이진 단위, 네트워크 전송량은 MB/s 등 십진 단위입니다.
- 링크 사용률은 수신·송신 각각의 링크 속도 대비 사용률 중 큰 값입니다. 인터넷 회선 계약 속도 대비 비율이 아닙니다. 링크 속도를 알 수 없으면 `—`를 표시합니다.
- 인터넷 연결·로컬 연결만·연결 끊김·확인 불가를 구분합니다. **준비 중 / 지원 안 됨 / 접근 거부 / 일시 오류**는 툴팁에서도 확인할 수 있습니다.

## 빌드와 검증

개발에는 .NET 10 SDK가 필요합니다. `global.json`의 SDK 버전을 확인하세요. PowerShell에서 저장소 루트를 기준으로 실행합니다.

```powershell
.\build\build.ps1
# SDK 경로를 직접 지정할 때
.\build\build.ps1 -DotnetPath 'C:\Tools\dotnet\dotnet.exe'
```

스크립트는 지정 경로 → 저장소의 `.tools\dotnet\dotnet.exe` → PATH → 참고 프로젝트 `..\ai_sa\.tools\dotnet\dotnet.exe` 순서로 SDK를 찾습니다. 참고 프로젝트의 파일은 변경하지 않습니다. 최초 자체 포함 배포에는 런타임 패키지 복원을 위한 네트워크가 필요할 수 있습니다.

솔루션 Release 빌드, Core 자동 검증, `win-x64` 자체 포함 게시, 격리된 UI 스모크 검증을 순서대로 실행합니다. 검증 실패 시 새 ZIP을 배포 결과로 승격하지 않습니다. UI를 실행할 수 없는 빌드 환경에서는 `-SkipUiSmoke`를 명시할 수 있지만, 배포 전 UI 검증은 별도로 실행해야 합니다.

결과물은 `.artifacts\publish\ResourceStatus.exe`와 `.artifacts\ResourceStatus-0.0.2-win-x64.zip`입니다. 빌드 중간 파일은 `.artifacts\build\…`로 격리합니다. 매번 빈 staging 폴더에서 게시하고 이전 publish 폴더는 `.artifacts\previous-publish-…`에 보관합니다. 검증 결과는 `.artifacts\validation\…`에 남습니다.

설치 파일은 위 빌드가 성공한 뒤 Inno Setup 6으로 만듭니다. 결과물은 `.artifacts\ResourceStatus-0.0.2-win-x64-setup.exe`입니다.

```powershell
.\build\build-installer.ps1 -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
.\build\test-installer.ps1
```

`v0.0.2`처럼 `Directory.Build.props`의 버전과 일치하는 태그를 푸시하면 GitHub Actions가 빌드와 검증 후 설치 파일·포터블 ZIP·SHA-256 체크섬을 GitHub 릴리즈에 게시합니다.

Core 검증만 실행하려면 다음 명령을 사용합니다. 별도 테스트 프레임워크 패키지를 설치하지 않는 콘솔 테스트이며 실패하면 0이 아닌 종료 코드를 반환합니다.

```powershell
dotnet run --project .\tests\ResourceStatus.Tests\ResourceStatus.Tests.csproj -c Release
```

데모는 가상 데이터를 표시합니다. 기본적으로 `%LocalAppData%\ResourceStatus.Demo`를 사용하며 실제 위젯 설정과 자동 시작 등록을 변경하지 않습니다. 다음과 같이 설정 경로를 추가로 격리할 수 있습니다.

```powershell
$exe = (Resolve-Path '.\.artifacts\publish\ResourceStatus.exe').Path
& $exe --demo --settings-dir "$PWD\.artifacts\demo-settings"
& $exe --demo --expanded --settings --settings-dir "$PWD\.artifacts\demo-expanded-settings"
```

UI 스모크 검증은 데모 데이터로 표시 옵션·두 모드·설정 저장·접근성 체크박스 조작을 확인하고 보고서를 기록한 뒤 종료합니다. 먼저 독립적인 단색 도형으로 WPF 이미지 렌더링이 가능한지 확인하며, 지원되지 않는 세션에서는 픽셀·스크린샷 검사를 명시적으로 건너뛰고 실제 컨트롤·배치 검사는 계속합니다. 보고서의 `renderingAvailable`과 `skipped`를 함께 확인하세요. 배포 EXE 자체를 검사하려면 다음과 같이 실행합니다.

```powershell
$arguments = '--smoke-test --settings-dir "' + "$PWD\.artifacts\manual-smoke\settings" + '" --output "' + "$PWD\.artifacts\manual-smoke\smoke.json" + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
$process.ExitCode
```

실제 시스템 수집기 진단은 다음과 같습니다. `--probe`는 위젯 화면 없이 CPU·RAM·GPU·네트워크 카운터와 수집기의 비용을 기록합니다. **수집기 CPU 측정값은 화면 렌더링을 포함한 위젯 전체 CPU 사용률과 다릅니다.**

```powershell
$arguments = '--probe --seconds 15 --output "' + "$PWD\.artifacts\probe.json" + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
$process.ExitCode
```

실제 다중 모니터·모니터 분리, 물리 GPU와 가상 머신 GPU, 절전 복귀, 작업 관리자와 실제 부하 비교는 장비별 확인이 필요합니다. 자동 검증만으로 이러한 동작이나 위젯 전체 평균 CPU 1% 미만을 보장하지 않습니다.

실행 중인 위젯 전체 프로세스의 CPU·메모리를 10분간 측정하려면 다음 명령을 사용합니다. 선택한 위젯을 표시한 상태로 유지하세요. CPU 비율은 논리 프로세서 수로 정규화한 시스템 전체 대비 값입니다.

```powershell
.\build\measure-widget.ps1 -TargetProcessId <위젯 프로세스 ID> -Seconds 600
```

현재 개발 환경에서는 실제 CPU·RAM·네트워크 수집을 확인했습니다. 2026-10-19에 원격 데스크톱을 연결한 상태에서 작은·큰 위젯과 설정 화면을 캡처하고 육안으로 확인했습니다. 테마 전환·설정 저장·버튼 정렬·픽셀 렌더링을 포함한 UI 검증 결과는 `.artifacts/validation`의 최신 `smoke.json`에서 확인할 수 있습니다. 실제 캡처는 `.artifacts/screenshots`에 있습니다. 원격 데스크톱 연결이 끊어지면 화면 캡처와 WPF 픽셀 렌더링이 빈 이미지로 반환될 수 있습니다. GPU는 가상 머신의 기본 그래픽 드라이버로 표시되어 사용률을 검증할 수 없으며, 물리 GPU·다중 모니터·실제 화면 배율 변경은 별도로 확인해야 합니다.

별도 .NET 없는 환경을 확인하려면 깨끗한 Windows 11 x64 VM에 ZIP만 복사하여 압축 해제한 뒤 EXE를 실행하세요. 데모와 UI 스모크가 실행되는지, 실제 CPU·RAM·네트워크가 갱신되는지 확인하고 종료 코드를 기록합니다. VM에서 지원하지 않는 GPU 지표는 별도로 물리 GPU에서 검증해야 합니다. 빌드 스크립트의 런타임 파일 확인은 이 깨끗한 환경 검증을 대신하지 않습니다.
