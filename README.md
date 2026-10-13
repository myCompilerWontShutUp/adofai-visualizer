# ADOFAI Practice Visualizer

ADOFAI(얼불춤) 커스텀 맵을 읽어 **Falling Note**로 보여주는 개인용 Windows 연습 도구입니다. C# / .NET 9 / WPF, 외부 NuGet 없음. 음악은 재생하지 않고, 게임 조작·DLL Injection·메모리 접근·자동 입력을 쓰지 않습니다. 맵 폴더는 읽기만 합니다.

## 설치 (`Install.cmd`)

`Install.cmd` 하나를 받아 더블클릭합니다.

1. git과 .NET 9 SDK가 없으면 winget으로 설치할지 묻습니다.
2. 설치 폴더를 묻습니다(Enter면 `C:\Users\<이름>\AdofaiVisualizer`).
3. 비공개 저장소를 받습니다(처음 한 번 GitHub 로그인).
4. 빌드하고 시작 메뉴·바탕화면 바로가기를 만들지 묻습니다.

이미 받은 폴더 안에서 실행하면 빌드와 바로가기만 합니다. 손으로는 `git clone` 뒤 `.\build.ps1 -Configuration Release`와 같습니다(.NET SDK 9.0.305 이상의 9.0, 패키지 복원 없음).

**게임 링커**(게임 시작 동기화용 모드)는 빌드된 채로 `plugins/linker/`에 들어 있어, 앱 콘솔(Ctrl+T)에서 `install linker`만 치면 됩니다(게임에 Unity Mod Manager 필요).

## 업데이트 (`update`)

올리는 것은 편집기의 git으로 하고, 다른 PC는 받기만 합니다. 앱 콘솔에서 `update`를 치면 최신인지 확인하고, 새 버전이 있을 때만 묻고 앱을 닫았다가 새 버전을 빌드해 다시 엽니다(설치된 링커도 함께). 터미널에서는 `.\update.ps1 -Check` / `.\update.ps1`입니다. 직접 고친 내용이 있으면 거절하고, `update -force -version:1.0.0`은 언제나 물은 뒤 그 버전으로 갑니다. `scannow`는 무결성 검사, `install -reinstall <what>`은 다시 쓰기입니다.

앱 데이터(맵 사본·수정본·설정·메모·핀)는 저장소 밖 `%LOCALAPPDATA%\AdofaiVisualizer\`에 있어 올라가지 않고, 같은 PC의 모든 복사본이 함께 씁니다. 새 PC에서는 맵을 한 번 동기화합니다.

## 제거 (`Uninstall.cmd`)

앱 안에서는 지울 수 없습니다. 앱과 게임을 닫고 시작 메뉴의 "Uninstall ADOFAI Visualizer" 또는 `Uninstall.cmd`를 실행합니다(PowerShell: `.\uninstall.ps1 [-Plan] [-Part plugins,data,app]`).

- **플러그인**: 게임의 `Mods\AdofaiVisualizerSync` 폴더(다른 모드는 그대로).
- **데이터**: `%LOCALAPPDATA%\AdofaiVisualizer`(데이터 폴더를 따로 골랐다면 앱이 만든 것만).
- **이 복사본**: 폴더와 바로가기. 올리지 않은 작업이 있으면 `-Force` 없이는 지우지 않습니다.

하나씩 `y`를 쳐야만 지웁니다. 스팀 워크숍 원본 맵은 어떤 경우에도 지우지 않고, git·.NET SDK도 남깁니다.

## 빌드

```powershell
.\build.ps1                          # Debug
.\build.ps1 -Configuration Release   # Release
.\build.ps1 -Test                    # 빌드 + 회귀 테스트
```

`dotnet build`를 직접 쓰지 마세요. 스크립트가 도구 홈·NuGet 캐시·임시 경로를 프로젝트 내부 `.local/`로 돌립니다. 실행 파일은 `src/Visualizer.App/bin/<구성>/net9.0-windows/Visualizer.App.exe`입니다.

## 사용 방법

1. **설정 → 고급 설정**에서 맵 폴더를 지정하고, **곡 리스트** 카드에서 동기화합니다(스팀 폴더 → 앱 사본, 실시간 감시 없음).
2. 곡 칸에 제목을 입력해 고르고 **불러오기**. **F8**로 켜고 끕니다.
3. **Enter**로 준비(재설정), **J**로 시작. 두 번째 이후 체크포인트나 구간에서 시작하면 첫 노트 레인 키로 시작합니다.
4. 레인 키는 기본 `A S D F J K L ;`(타일 1~8). 누르면 레인이 빛나고 누른 노트가 어두워지며, 놓친 노트는 빨갛게 표시됩니다. 시각 피드백일 뿐 게임 판정이 아닙니다.
5. 노트 영역 위에서 배속·구간(타일 번호)·슬롯(수정본)을 고릅니다. **Esc**는 일시정지(다음 노트를 판정선에 맞춤), 일시정지 중 레인 키로 재개합니다.
6. **Ctrl+E**는 곡 수정(레인·동타), **Ctrl+T**는 명령어 콘솔(`cmd`로 명령 목록, 설명은 `docs/console.md`).

규칙: [노트 규칙](docs/note-rules.md) · [체크포인트](docs/checkpoints.md) · [환경 대응](docs/environment-compatibility.md) · [파서](docs/adofai-parser.md) · [게임 동기화](docs/game-sync-plan.md).

## 맵 보호

Workshop(기본 `C:\Program Files (x86)\Steam\steamapps\workshop\content\977950`)과 지정한 모든 맵 폴더는 읽기 전용입니다. `IReadOnlyMapSource`는 원문 읽기만 제공하고, 앱은 앱 저장소에만 쓰며 맵 경로와의 동일·상하위·링크 경로를 거부합니다. 확인되지 않은 타이밍 규칙은 추측해 구현하지 않습니다. 작업 규칙은 [AGENTS.md](AGENTS.md).
