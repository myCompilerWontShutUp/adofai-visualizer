# CLAUDE.md

ADOFAI(얼불춤) 커스텀 맵을 Falling Note로 보여주는 개인용 Windows 연습 Visualizer. C# / .NET 9 / WPF, 외부 NuGet 없음.

**작업 규칙과 현재 동작의 원본은 [AGENTS.md](AGENTS.md)이며 충돌하면 AGENTS.md를 따릅니다.** 이 문서는 코드를 찾고 고치는 데 필요한 지도(어느 클래스·메서드에 무엇이 있는가)만 담습니다.

토큰 절약 규칙: 큰 파일(`note-rules.md`, `adofai-parser.md`, `VisualizerSettings.cs`, `NoteField.cs`, `SettingsWindow.*`, `AdofaiTimelineParser.cs`, `CmdSession.cs`)은 통째로 읽지 말고 검색으로 필요한 구간만 봅니다. `docs/handoff-history.md`는 사용자가 과거 경위를 물을 때만 엽니다.

@AGENTS.md

---

## 1. 빌드·테스트·파일 편집

**`dotnet build`를 직접 실행하지 말고 반드시 `build.ps1`을 씁니다.** 스크립트가 DOTNET_CLI_HOME·NuGet 캐시·TEMP·APPDATA를 프로젝트 내부 `.local/`로 돌린 뒤 복원합니다. 직접 빌드하면 프로젝트 밖에 캐시가 쓰입니다(IDE 자동 빌드도 마찬가지).

```powershell
.\build.ps1                                   # Debug
.\build.ps1 -Configuration Release            # Release (Launch-Demo.cmd가 사용)
.\build.ps1 -Test                             # 빌드 + 회귀 테스트 (외부 맵 없음)
# 사용자가 명시적으로 요청할 때만:
.\build.ps1 -Test -ReadOnlyMapRoot '<맵 루트>' [-CompareGameRules]
```

```powershell
.\build-mod.ps1 -Configuration Release            # 게임 안에서 도는 읽기 전용 모드 (게임 폴더 필요)
.\build-mod.ps1 -Configuration Release -Install   # 위를 빌드하고 <게임>\Mods\AdofaiVisualizerSync\에 넣음
```

- 모드는 `Visualizer.sln`에 **없습니다**. 게임 어셈블리를 참조하므로 게임이 없는 PC에서도 `build.ps1`이 그대로 돌아야 하기 때문입니다. `-Install`은 게임 폴더에 쓰는 유일한 동작이라 사용자가 요청할 때만 씁니다.
- SDK는 `global.json`에서 9.0.305로 고정, `NuGet.Config`는 소스를 비워 오프라인입니다.
- `Directory.Build.props`에서 `Nullable`·`ImplicitUsings`·**`TreatWarningsAsErrors`**·`Deterministic`을 켰습니다. 경고 하나도 빌드 실패입니다. 앱 버전 `<Version>`과 `<VersionDate>`도 여기 있습니다.
- 실행 파일: `src/Visualizer.App/bin/{Debug|Release}/net9.0-windows/Visualizer.App.exe`. 캐시 루트를 찾으려면 프로젝트 안에서 실행해야 합니다.
- 사용자가 앱을 실행 중이면 Release 출력 폴더가 잠깁니다. **앱을 종료하지 말고** Debug로 검증한 뒤 사용자에게 알립니다.
- `build.ps1 -Test`는 실패한 테스트의 첫 줄에서 멈춥니다. 실패 전체를 보려면 같은 환경 변수로 `tests/Visualizer.Tests/bin/Debug/net9.0-windows/Visualizer.Tests.dll <산출물 폴더>`를 직접 실행해 `FAIL` 줄을 봅니다.
- **파일 편집**: Edit/Write 도구나 Python(`encoding='utf-8', newline=''`, 줄바꿈은 CRLF 유지)으로 고칩니다. PowerShell 5.1의 `Get-Content`/`Set-Content`는 BOM 없는 UTF-8 한글을 깨뜨리고, `Copy-Item`은 수정 시각까지 복사해 증분 빌드가 바뀐 파일을 건너뜁니다.
- 아이콘 글리프(U+E7xx)·제어 문자를 C# 문자열에 그대로 넣지 않습니다. `char.ConvertFromUtf32(0xE768)`, XAML은 `&#xE768;`를 씁니다.

## 2. 솔루션 구조

```text
App ──► Application ──► Core        Infrastructure ──► Application        Tests ──► App
```

```text
src/Visualizer.Core/            순수 모델·규칙 (WPF·파일시스템 비의존)
  Maps/                         ParsedTimeline(불변 hit time·체크포인트·HitKind, CountPressedHits·CountHits·CheckpointCount), RawMapDocument
  Settings/                     VisualizerSettings(불변 record, Validate/ValidateFields/EnsureValid/HasSameNoteRulesAs),
                                DefaultSettings.jsonc(+.cs) 모든 초기값, SettingsJson(내보내기·부분 병합 가져오기·예전 키 읽기),
                                SettingsFields(이름으로 읽기·쓰기·Restore), ColorText
  Localization/                 Languages.cs + Languages/*.json (파일 이름 = 언어 id), ReleaseNotes(언어 파일의 ReleaseNotes.<버전>, 보여 줄 버전 고르기)
  Visualization/                DynamicTimingSelector(간격→모드) · TilePatternCursor(전환·회전) · NoteGenerator(묶음→모드→커서→배치)
                                SimultaneousNotePlanner(동타 배치, NotePlacementException) · MagicCircleFinder · NoteLayout · NotePosition
src/Visualizer.Application/     계약·사용 사례
  Maps/                         IReadOnlyMapSource/Directory, IMapTimelineParser(ParseForced), MapSyncService(동기화·ForceAsync), MapFileRules(backup.adofai 제외)
  Cache/                        IMapCache, MapCacheEntry (읽기/쓰기 계약 분리)
  Library/                      SongPreference·ISongPreferenceStore(숨김·표시 이름·직접 순서), SongOrdering(정렬·곡 그룹·원곡 아래 수정본), MapKind, SongIds
                                NoteEdit·NoteEditSession(수정 자료와 연산), NoteEditLayout(규칙 결과에 레인·묶음 덮기)
  Sync/                         GameSyncLink(게임 모드가 보낸 값 검사·링크 측정·최신 읽기) · GameTileCheck(게임의 타일 시각과 우리 hit 대조)
                                GameSyncFollower(오프셋 확정·매 프레임 재생 지점만 이동)
  Playback/                     PlaybackSession(Off/NoTimeline/Waiting/Playing/Paused, Reset·ApplySettings·GenerateSongNotes)
                                NotePressJudge(시각 전용 판정) · RulePreviewTimeline(합성 미리보기)
  Input/                        InputPolicy, IInputMonitor, KeyPressTracker
src/Visualizer.Infrastructure/  파일·Windows 어댑터
  Maps/ReadOnlyDirectoryMapSource.cs   .adofai 열거·읽기만, 루트 탈출·reparse point 거부
  Cache/WorkspaceMapCache.cs           <저장소>/map-cache/library.json, 경로 보호, tmp → 원자 교체
  Storage/AppStorage.cs                앱 전용 저장소 결정(모든 복사본이 함께 쓰는 %LOCALAPPDATA%\AdofaiVisualizer · 지정 폴더, SharedRoot·
                                       RepositoryStoreFor), settings.json 읽기·쓰기, 기본 위치의 data-root.txt로 지정 폴더 기억,
                                       last-version.txt(업데이트 소식을 본 버전)·RanBefore
  Storage/StoreMove.cs                 1.0.0 전의 <저장소>/.local/app-data를 공유 저장소로 한 번 복사(원래 폴더는 그대로). 곡 id가 맵 경로
                                       해시라 캐시·songs.json·song-ids.json·song-marks.json·edit.json의 id를 새 경로로 다시 씀
  Storage/MapLibrary.cs                <저장소>/maps/{base,custom,edited,removed} (.adofai 복사·재구축·삭제·보관, 종류·슬롯 판별)
  Storage/NoteEditStore.cs             <저장소>/maps/edited/<곡>/<슬롯>/{원본.adofai, edit.json}
  Storage/WorkspaceSongPreferences.cs  <저장소>/songs.json (곡 리스트 선택)
  Storage/SongIdBook.cs                <저장소>/song-ids.json (콘솔용 8자 id)
  Storage/SongMarkBook.cs              <저장소>/song-marks.json (곡 목록의 new 표시, 마지막으로 불러온 곡)
  Storage/ReportBook.cs                <저장소>/reports/*.txt (콘솔 report 메모. 모든 .txt가 메모, 기록은 written.mark·done.mark —
                                       같은 .txt였을 때 메모로 섞여 나왔음). 버전으로 지우지 않음, Write가 done을 지움(다시 적으면 열림),
                                       SetDone/DoneIn/ClearDone/Rename. 터미널 쪽 같은 일은 루트의 report.ps1
  Parsing/AdofaiTimelineParser.cs      CurrentVersion·FormatVersions·허용 이벤트 목록·ParseForced (근거는 docs/adofai-parser.md)
  Parsing/AdofaiJson.cs                문자열 안의 제어 문자·생략된 쉼표를 메모리에서만 보정 (게임은 GDMiniJSON으로 읽으므로)
  Input/                               WindowsRawInputMonitor(관찰 전용), WindowsForegroundObserver, MultimediaScheduling(MMCSS 등록)
  Sync/NamedPipeGameSyncServer.cs      게임 모드가 쓰는 명명 파이프를 읽기만 (JSON 한 줄 → GameSyncLink)
  Sync/LinkerInstaller.cs              콘솔 install linker가 쓰는 곳: 게임 폴더·모드 파일 찾기, UMM 여부, 설치된 버전, 두 파일만 쓰기
src/Visualizer.App/             WPF 조립 지점
  Icon/                         icon.png(사용자 제공 원본) → icon.ico(16~256px, csproj ApplicationIcon. 창은 exe 아이콘을 물려받음)
  Themes/                       Theme.xaml(공용 스타일), ThemeCatalog.cs(+ PrimaryBrush·ToggleOnBrush 계산) + Palettes/*.json, SpaceBackdrop
  Views/                        MainWindow(480×800, 서비스 조립·Raw Input 훅·프레임 루프·검색창 키), MainWindow.Console.cs(ICmdHost 구현)
                                SettingsPanel(창이 아니라 UserControl, 메인 창의 SettingsHost 안에서 열림 · .Rows 행 만들기/.Form 읽기·쓰기/.Json 고급·JSON)
                                + SettingsLayoutStore(카드 열림 상태)
                                SongListWindow(곡 리스트 상세, 검색 `Matches`·곡 제거 `RemoveSong`/`AskRemove`/`RemoveRow`) · SongDetails(곡 정보 문구 한 곳) · NoteEditWindow + EditSlotDialog + EditKeysWindow(곡 수정, 창 하나만,
                오른쪽에서 밀려 나오는 설정 패널 ShowPanel/GhostNotes)
                                CmdWindow(입력 칸 없는 터미널 화면, 기록 위에 ReportEditor 층) · ReportEditor(report 메모: 모드·상태줄·: 명령,
                                [Y/n]은 글자를 모아 Enter에서만 적용 — NORMAL의 안 쓰는 키는 false를 돌려줘야 텍스트 이벤트가 살아 `:`가 옴)
                                HelpWindow · ConfirmDialog(risky = 주황 아웃라인)
                                MainWindow.UseAppStore: 테스트가 실제 저장소를 읽고 쓰지 않도록 끄는 스위치
                                MainWindow.StoreForTests: scannow처럼 저장소를 읽는 검사를 테스트 폴더로 돌리는 스위치
                                MainWindow.Scan.cs: scannow 영역별 검사(app·source·linker·maps·edits·data)와 install -reinstall
                                MainWindow.PlaybackClock: 창의 ViewModel이 쓸 시계(테스트가 3초 규칙을 기다리지 않게)
  Commands/                     콘솔(문구 영어 고정, 명령표 원본은 docs/console.md): CmdCatalog(명령 표·옵션 묶음·키워드·파서·Keyword/NameClash),
                                CmdSession(동작·[Y/n]·번호 선택·설정 먼저 닫기·Tab 후보·copy/prompt/report 편집기), SettingPaths(set/get 이름·값·잠금)
  ViewModels/MainViewModel*.cs  본체(설정·이력·재생) .Songs(목록·검색·불러오기·MoveSong) .Placement(TryPrepare·제안) .Overlay(곡 정보)
  Rendering/                    NoteField(Tick으로 필요할 때만 다시 그림), NoteFieldPalette(Freeze한 브러시), NoteEditField(수정 화면, RuleNotes/ShowRuleNotes로 규칙 노트를 뒤에),
                                FrameStats(프레임 간격·자체 작업 시간·GC·렌더 티어, 콘솔 fps)
  Input/                        ControlBindings, KeyNames, KeyCaptureBox, PlaybackButton
  Localization/UiText.cs        언어 파일 바인딩 (XAML Text[key], 코드 Format)
tests/Visualizer.Tests/         실행형 하네스 (xUnit 없음)
docs/                           규칙·근거 문서 (5절)
tools/GameSyncMod/              게임 안에서 도는 읽기 전용 모드 (net48, UMM). Main(진입·OnUpdate) · GameReader(리플렉션으로 값·타일 시각) ·
                                KeyViewerFocus(승인된 단 하나의 쓰기: 전경이 아닐 때 JipperResourcePack 키뷰어 GameObject만 끔) ·
                                SyncWriter(별도 스레드 파이프 쓰기). 솔루션에 없고 `build-mod.ps1`로 빌드·설치.
                                `Info.json`은 빌드가 만듭니다(csproj의 WriteModInfo) — 버전은 Directory.Build.props 하나에서
update.ps1                      저장소 쪽 일을 하는 유일한 스크립트: -Check(state 줄) · 받기/-Force -Version(버전은 <Version>을 바꾼 커밋으로 찾음) ·
                                -Reinstall source,app,linker · -Scan(git status) · -LinkerGame(링커 빌드·설치) · 빌드 · 다시 띄우기.
                                콘솔 `update`·`version`·`scannow source`·`install -reinstall`이 그대로 실행
.example/main.adofai            읽기 전용 예제 (Plum - R)
.local/                         도구 홈·캐시·테스트 산출물·백업 (gitignore)
```

## 3. 데이터 흐름과 코드 위치

```text
시작      AppStorage.Open → settings.json 있으면 SettingsJson.Import로 복원 (없거나 깨졌으면 기본값 + 안내) → ForgetSettingsHistory
적용      설정 변경 → MainViewModel.SettingsHistory 기록 → SettingsCommitted → MainWindow.SettingsCommitted(바인딩 갱신 + 저장)
Map Sync  ReadOnlyDirectoryMapSource → AdofaiTimelineParser → MapSyncService → WorkspaceMapCache (앱 저장소에만 기록)
Load Song 캐시 항목 → LoadAsked(배속 초기화) → LoadEntry → PlaybackSession.LoadParsedTimeline (원본 재파싱 없음)
Reset     ① 체크포인트 첫 hit 기준 정규화 ② 배속으로 나눔 ③ NoteGenerator ④ StartDelayMs 더함
          실패(NotePlacementException) → Notes 비움, Start 차단, 원본 타임라인 유지
Start     시계 기준점만 기록 (Paused는 같은 지점에서 재개)
프레임    CompositionTarget.Rendering → MainViewModel.RefreshPlaybackInfo() → NoteField.Tick()
키 입력   Raw Input → KeyPressTracker → (액션) ControlBindings → InputPolicy → 명령 / (레인) MainViewModel.PressLane
```

- **판정선**: progress = `1 - (hitMs + globalOffset + mapOffset - elapsedMs) / travelTimeMs`. 0이 상단, 1이 판정선입니다. 판정선은 하단 제거선보다 `JudgmentLineHeightDip` 위에 있고(창이 작으면 노트 영역의 60%까지), 노트는 판정선을 지나 같은 속도로 내려가 제거선을 지나야 사라집니다.
- **체크포인트 자동 적용**: 그 타일의 노트를 누른 순간, 누르지 않으면 입력 인정 범위가 끝날 때. `AutoCheckpointAdvance`가 꺼졌거나 배속≠100%이거나 구간 시작이면 선택을 유지하고 토글이 잠깁니다. 수동 선택(기본 `+`/`-`)은 즉시 정지 배치하며 목록 양 끝에서 순환합니다(곡·테마 ◀ ▶도 동일).
- **ApplySettings**: `HasSameNoteRulesAs`가 다르면 노트를 다시 준비하고(이전 배치가 실패해 비어 있어도 Reset), 그 밖의 설정(표시 보정·두께·이동 시간·언어·키·자동 진행·판정선 높이·입력 인정 범위 등)은 노트·시계·일시정지 상태를 유지합니다. `RollBack`은 마지막 단계의 바뀐 항목만 `SettingsFields.Restore`로 되돌립니다.
- **시작 위치**: 구간(연습 타일)이나 두 번째 이후 체크포인트에서 시작하면 곡 처음부터 만든 배치를 **잘라 쓰고** 첫 노트 레인의 키로 시작합니다. 처음부터만 Initial Tile·시작 키입니다. 그래서 곡 앞부분에 배치 불가 동타가 있으면 뒤 체크포인트도 준비가 중단됩니다.
- **입력 정책**: 전역 입력 범위를 켜면 ADOFAI 또는 이 비주얼라이저가 전경일 때만 반응합니다. UI 입력 편집 중·설정 창 열림·콘솔 창 활성이면 반응하지 않습니다. 레인 키는 시작 키와만 겹칠 수 있고 같이 눌리면 Start를 먼저 처리합니다. Esc는 일시정지(관찰만)이며 일시정지 중 레인 키는 ±1초 안의 그 레인 노트로 재개합니다.
- **콘솔의 진행률 바**: `CmdSession.Job`·`Busy`·`JobText()`·`RunJob`이 한 벌입니다. `RunJob`은 프롬프트를 막대로 바꾸고 호스트에 `IProgress<double>`과 끝낼 때 쓸 콜백을 넘기며, `Submit`은 `Busy`면 입력을 버립니다(`CmdWindow.Render`가 막대를 그리고 키·텍스트 입력을 막습니다). 호스트는 `ICmdHost.SyncMaps`/`Resync`(→ `MainWindow.Run`이 끝나면 곡 수를 알림)와 `InstallLinker(place, progress)`입니다. 진행률은 `MainWindow.CopyAndScanAsync`의 `Step`이 복사 40% + 세 폴더 파싱 60%로 계산하고 같은 값을 메인 화면 퍼센트로도 씁니다.
- **Ctrl+E**: `MainWindow.IsEditShortcut`. 메인 창 `PreviewKeyDown`(단, `Busy`면 넘김 — 터널링이라 창 처리기가 먼저 돌기 때문) → `EditLoadedSong()`, 설정판 `PreviewKeyDown` → `EditSong()`(설정을 먼저 닫음).
- **곡 끝 알림**: `PlaybackSession.PastLastNoteMs`(재생 중이고 남은 노트가 없을 때 마지막 노트가 지난 지 얼마인지) + `MainViewModel._lastInputMs`(`NoteInput()`이 찍음: 앱이 보는 키·창 클릭·버튼)로 `RefreshPlaybackInfo`가 `ShowSongEndNotice`를 정합니다(1초, `SongEndQuietMs`). 안내가 떠 있을 때 초기화 키는 `MainViewModel.ResetFromKey(offered)`가 버튼과 같은 길로 보냅니다 — 창의 키 처리기가 `NoteInput()`으로 안내를 내리기 **전에** `offered`를 읽어 넘깁니다. 버튼은 `RestartFromBeginning`·`RestartAtLastCheckpoint` → `StartOver(체크포인트)`(같은 번호여도 다시 준비하고 구간을 지움). XAML은 `MainWindow.xaml`의 `SongEndCard`이고, 두 번째 버튼은 자리를 지켜야 해서 `Views/HiddenWhenFalse.cs`로 **숨김**만 합니다. 두 `Show…`는 `PlaybackProperties`에 있고 테스트는 `MainViewModel.PlaybackNames`로 그것을 확인합니다. 시간을 밀어야 하는 테스트는 `new MainViewModel(clock)`으로 자기 시계를 넘깁니다.
- **Ctrl+T**: `CmdWindow.IsShortcut`. 메인·설정 창의 `PreviewKeyDown` → `MainWindow.ToggleConsole`(없으면 `ShowConsole`, 있으면 `CmdWindow.ToggleMinimized`), 콘솔 안에서는 `ScreenKeyDown` → `ToggleMinimized`. 콘솔은 메인 창이 뜬 방식(`ShowActivated`)대로 열립니다.
- **일시정지 정렬**: `MainViewModel.AlignPausedNote`가 `NotePressJudge.FirstUnpressed`(누름 기준, 시각 아님)로 노트를 고르고 `PlaybackSession.MovePaused`로 멈춘 위치만 옮깁니다. `PauseFromKey`와 `PauseResume`의 멈추는 쪽에서 부르며, 설정은 `AlignNextNoteOnPause`입니다.
- **노트 영역 위의 조작**(0.9.1, 0.9.2 개편): 배속(`SlowerButton`·`SpeedBox`·`FasterButton`)·구간(`SectionField`)·슬롯(`SlotPicker`)은 `MainWindow.xaml`의 `FieldControls`(반투명 `Border` 안의 2행 3열 `Grid`)에 있고, `MainViewModel.ShowFieldControls`(곡이 있고 `Waiting`)일 때만 보입니다. 슬롯은 `SlotOptions`/`SelectedSlot`/`ShowSlotPicker`이며 setter가 바로 `LoadCached`합니다. 수정 버튼은 설정의 실험적 카드(`SettingsPanel._editSongButton` → `EditSongRequested`)로 갔습니다. 배속은 `StepSpeed`/`ApplyTypedSpeed`/`ApplySpeed` → `ApplySettings`(기록·저장 포함)이고 `SpeedText`는 단위까지 붙여 돌려주므로(`100%`) `ApplyTypedSpeed`가 `%`를 떼고 읽습니다. 치는 동안 값이 되돌아가지 않도록 setter에서 알리지 않는 것은 그대로입니다.
- **프레임 스케줄링**: `MainWindow.InitializeInput`이 `MultimediaScheduling.Register()`로 프레임 스레드를 MMCSS에 넣고 `Closed`에서 같은 스레드로 돌려줍니다(실패해도 `Problem`만 남고 동작은 같음). GC 지연 모드는 `App.OnStartup`의 `GCSettings.LatencyMode`이며, 둘 다 `fps` 마지막 줄에 상태가 나옵니다.
- **프레임 진단**: `MainWindow.RenderFrame`이 도착 시각·`RenderingEventArgs.RenderingTime`·자기 작업 시간을 `FrameStats.Add`에 넣고, 콘솔 `fps`가 `ICmdHost.FrameStats` → `FrameStats.Lines`로 출력합니다(영어 고정). 늦은 프레임의 합성기 간격도 같이 늘어났으면 합성기가 안 준 것(`SkippedFrames`), 합성기는 고른데 도착만 늦으면 스케줄링(`HandoffMaxMs`), 작업 시간이 간격의 절반을 넘으면 그리기 쪽입니다. 같은 `RenderingTime`이 두 번 오면 한 프레임으로 셉니다.
- **미니게임 표시**: 파서가 `ParsedTimeline.Sections`(FreeRoam 구간, hit 시각)를 만들고 캐시가 그대로 싣습니다. `PlaybackSession.SectionAt`이 시작·배속·표시 보정을 적용해 재생 시각으로 바꾸고, `MainViewModel.RefreshPlaybackInfo`가 `SectionNotice`를 채우면 `MainWindow.xaml`의 노트 영역 위 문구가 보입니다.
- **배치 실패 제안**(`MainViewModel.Placement`): 원곡은 여분 레인으로 곡 전체가 배치되면 `ConfirmExtraLanes`만 묻고, 아니면 곡 전체를 배치하는 가장 작은 더 큰 최대 타일 수를 `ConfirmWiderLanes`로 묻습니다. 수정본(`Playback.Layout is not null`)은 여분 레인을 묻지 않고 `EditedLanesQuestion`("이 맵은 n레인으로 제작…")으로 최대 레인 수만 묻습니다. 곡·설정당 한 번이며, 수정본의 레인이 화면에 없을 때도 같은 경로입니다(`NoteEditLayout.Apply`가 화면 밖 타일을 거부, `Places`는 `Playback.Layout`을 넣어 시험).
- **곡 목록**: `MainViewModel.RebuildLibrary`가 숨긴 곡을 빼고 표시 이름(`DisplayName`, 수정본은 슬롯 접두어)을 덮은 뒤 `SongOrdering.Sort`로 줄 세웁니다. 곡 그룹이 전체면 `SongFamilies`(가족·슬롯)를 넘겨 원곡만 정렬하고 수정본을 붙이며, 숨긴 원곡 자리를 위해 숨긴 곡까지 정렬한 뒤 뺍니다. `AllSongs`는 숨긴 곡까지 원래 이름으로 갖고 있어 상세 창이 씁니다.
- **검색창**: `SongQuery`는 입력칸에 보이는 글자, `_filter`는 친 글자(고른 곡의 제목은 검색이 아님)입니다. 불러오기 대상은 `SongToLoad`, 방향키는 `PickInSearch`입니다. 편집 가능한 콤보박스가 Enter·방향키를 Preview 클래스 처리기에서 먼저 처리하므로, 메인 창 `PreviewKeyDown`이 출처가 검색창일 때 `SongBoxKey`로 먼저 받습니다(콤보박스 자체의 처리기로는 늦음). 목록은 `OpenSongList`가 열면서 전체 선택된 입력칸의 커서를 되돌립니다. 입력칸 포커스가 없을 때 `ShowSongTitleStart`가 긴 제목의 앞을 보여 줍니다.
- **불러오기**: 사용자가 부른 불러오기(`LoadSelectedSong`·`MoveSong`·`LoadExample`·`LoadChosen`)는 `LoadAsked`를 거쳐 배속을 초기화하고(`KeepSpeedOnLoad`, 콘솔 `-speed:keep`/`-speed:reset`), 가이드 버튼 복원은 `LoadCached`로 그대로 둡니다.
- **곡 리스트 상세 창**(`SongListWindow`): 맨 위 표시 항목 토글이 `SongListFields`를 정하고 행은 항상 그 순서로 한 줄에 그립니다(행 높이 `RowHeight` 고정). 숨긴 곡은 제목을 흐리게 해서 구분합니다. 원곡 아래 수정본 행은 `Row.Follows`라 끌 수 없고, `MoveRow(from, gap)`는 원곡과 수정본을 한 덩어리로 옮기며 수정본 사이의 틈은 `Snap`으로 덩어리 뒤로 보냅니다. 실제 드래그는 테스트에서 일으킬 수 없어 `MoveRow`로 검증합니다. 끌어 옮기면 정렬이 `Manual`로 바뀝니다. 맨 위 검색칸은 `_filter`·`Matches`로 행만 걸러내고(검색 중에는 손잡이를 잠금), 곡 제거는 `RemoveSong` 콜백(없으면 버튼도 없음)을 `AskRemove`가 `Confirm` 뒤에 부르며 테스트는 대화 상자를 누를 수 없어 `RemoveRow(index)`로 검증합니다. 콜백은 `MainWindow.RemoveListed`(수정본은 `Maps.Hold`로 보관, 나머지는 `Maps.Remove` + `Forget`)이고 `RemoveSong` setter가 `Build()`를 다시 부릅니다(생성자가 이미 행을 그린 뒤에 들어오므로).
- **설정은 창이 아닙니다**(0.8.9): `SettingsPanel`은 `UserControl`이고 `MainWindow.xaml`의 `SettingsHost`(오른쪽 정렬 `Border` + `SettingsSlide` 이동) 안에서 열립니다. 폭은 `SettingsShare`(0.9)·`SettingsLeastWidth`(400)로 **호스트가 정하고**, `SlideSettings`는 값을 먼저 넣고 애니메이션을 `FillBehavior.Stop`으로 덮으므로 프레임이 없는 테스트·캡처에서도 제자리에 있습니다. 창이 하던 일은 이렇게 바뀌었습니다: `DialogResult`·`Close()` → `Leaving` 콜백(`MainWindow.OpenSettings`가 받아 적용·정리), `OnClosing`의 질문 → `TryLeave`, `IsCancel` → 패널의 Escape 처리, `ConfirmDialog.Ask(this, …)` → `Window.GetWindow(this)`. 수정 창은 여전히 창이지만 `Show()`로 엽니다(모달 창은 앱의 모든 창을 잠가 콘솔까지 막음).
- **노트 영역 위의 글자**(0.9.2): 배경은 `OverlayBand`(`ShowOverlayBand` = 곡 정보 또는 조작) 하나이고 그 안의 `SongOverlayCard`·`FieldControls`가 각자 나타나고 사라집니다(사이에 틈 없음). `SectionNoticeCard`는 위쪽 별도 띠입니다. 모두 `MainViewModel.OverlayBackBrush`(글자색의 반대, 알파 178)를 깔고 **좌우로 늘어나며 모서리를 둥글리지 않습니다**(안쪽 내용만 `HorizontalAlignment=Center`). 마지막 줄은 `OverlayTileParts`(`OverlayPart(Text, Changed)`)를 `Views/OverlayLine.cs`의 첨부 속성이 `Run`으로 풀어 바뀐 조각에만 밑줄을 칩니다. **이 표시들의 `Show…` 속성은 `MainViewModel.PlaybackProperties`에 이름이 있어야 재생 상태가 바뀔 때 화면이 따라옵니다**(없으면 값만 맞고 화면은 그대로 남습니다).
- **설정판 미리 만들기**(0.9.1): `MainWindow.PrepareSettings`가 `ApplicationIdle`에 `SettingsPanel`을 하나 만들어 창 크기로 `Measure`·`Arrange`까지 해 둡니다(`_spareSettings`·`_spareFor`). `OpenSettings`는 `TakeSettings()`로 그 한 벌을 꺼내 쓰고(설정이 바뀌었으면 버리고 새로 만듦) 닫힐 때·`SettingsCommitted`에서 다시 준비합니다. **쓴 패널은 재사용하지 않습니다**(상태가 넘어가지 않게). 실측: 만들기 ~0.1초, 배치 ~0.3초, 두 번째도 비슷.
- **설정 검색**(0.9.1): `SettingsPanel.Search(text)`가 카드(`_sections`)를 돌며 행의 글자(`Words`: 라벨·상태 문구·`?` 설명)를 비교해 `Visibility`를 정하고, 맞는 행이 없는 카드만 숨깁니다. 검색이 펼친 카드가 사용자의 접힘 상태를 덮지 않도록 `_searching` 동안 `_layout.SetExpanded`를 건너뜁니다. XAML은 `SearchBox`·`SearchHint`·`SearchNone`입니다.
- **설정 미리보기**(0.8.9): 패널의 `PreviewRulesButton` → `SettingsPanel.ShowPreview` → `Previewing(BuildSettings())` → `MainWindow.ShowSettingsPreview`가 `RememberLoaded` 후 `MainViewModel.LoadPreview(pending)`로 **적용하지 않은 설정**을 합성 타임라인에 걸고 바로 `Start`합니다. 패널은 `SlideSettings(false)`로 비켜서지만 열린 상태 그대로이고(`_settingsPanel`이 남아 `SettingsHost.Child`도 유지), `PreviewBar`만 마우스를 받습니다. 돌아오기는 `EndSettingsPreview`(`RestoreLoaded` + 패널 복귀 + `PreviewVisuals` 재적용)이며 Escape도 같습니다.
- **메인 창의 입력 차단**: `MainWindow.Busy`(설정 패널 열림 = `_dialogOpen`, 또는 수정 창)일 때 `PreviewMouseDown`과 검색창 키를 스스로 막습니다. **`SettingsHost`와 `PreviewBar` 안은 예외**(그 안이 지금 쓸 수 있는 유일한 곳). 콘솔은 `ICmdHost.EditorOpen`으로 수정 창을 알고 `CmdSession.NotWhileEditing`의 명령을 거절합니다.
- **곡 수정**: 편집 자료는 `NoteEditSession`(선택·이동·병합 `CanMerge`·분해·`Flip`·되돌리기, 여는 순간 건너뛴 동타를 푸는 `Loosened`), 저장은 `NoteEditStore`(`SlotsOf`로 슬롯 정보, `SongFolderOf`로 곡 폴더 — 수정본이면 `MapLibrary.EditedSongOf`), 재생 반영은 `PlaybackSession.Layout` → `NoteEditLayout.Apply`입니다. 편집 화면은 **타일 번호가 아니라 레인 위치**로 움직이므로 `NoteEditSession.Lanes`를 거치며, 창은 항상 1~8(`MainWindow.AllLanes`)을 메인 레인 폭으로 엽니다. 키는 `NoteEditWindow.Press`(창이 활성일 때만), 휠은 `Wheel`입니다. 창의 `Confirm`은 테스트용 대체(null이면 실제 대화 상자)입니다.
- **맵 폴더**: 곡은 `maps/base`·`maps/custom`·`maps/edited` 세 폴더에만 있고, 캐시 항목의 `RootDirectory`가 종류를 정합니다(`MapLibrary.KindOf`). 동기화는 스팀 → custom 복사 후 세 폴더를 파싱하고, 앱 폴더 밖 캐시 항목은 정리합니다(`DropForeignEntriesAsync`). `resync`는 `.new`를 만들어 교체합니다. 곡 id는 `SongIds.Assign`이 동기화·시작 때 붙이고(`AssignIds`), 형식이 맞지 않는 옛 id는 다시 발급합니다.
- **마지막 곡**: `MainViewModel.LastSongId`는 `LoadAsked`에서 목록에 있는 곡만 기억하고 `SongMarksChanged`로 저장합니다. 시작할 때 `MainWindow.OnLoaded` → `OfferLastSong`이 `ConfirmDialog.Ask(..., details, detailsLabel)`로 묻고(상세 문구는 `SongDetails.Lines`, 곡 리스트 행과 같은 항목), 예면 `LoadChosen`입니다.
- **곡 목록 표시**: `RebuildLibrary`가 사본마다 `MapCacheEntry.Badge`(표시 전용, 캐시에 쓰지 않음)를 `MainViewModel.BadgeOf`로 채웁니다(forced → recently → new). `new`는 `CopyAndScanAsync`가 동기화 전후 캐시 id를 비교해 `MarkNew`, `recently`는 `SaveEdit` → `ScanEditedAsync`가 저장한 경로로 `MarkRecentEdit`, 둘 다 `LoadAsked`에서 지웁니다. `new`만 `NewSongsChanged` → `SongMarkBook`으로 저장합니다. XAML은 `MainWindow.xaml`의 `SongPicker` 항목 템플릿(`IsForced`면 `FireBrush`)입니다.
- **link auto·install linker**(0.9.2): 자동 따라가기는 `MainWindow.Console.cs`의 `_linkAuto`·`AutoFollowGame`이며 **프레임마다 부르는 `FollowGameSync` 맨 앞**에서 돌고, 실제 동작은 `ICmdHost.GameSyncFollow(true)`를 그대로 불러 규칙을 한 곳에 둡니다(`_autoTried`가 레벨·곡당 한 번만 시도하게 하고 `_autoNote`가 이유를 `link` 출력에 싣습니다). 시도 여부를 가르는 열쇠에는 노트 수가 아니라 **`PlaybackSession.Prepared`**(준비 횟수, 실패한 준비 포함)를 넣습니다 — 배속 때문에 거절당한 뒤 배속을 되돌리면 노트가 그대로라 다시 시도하지 않았습니다. 설치는 `CmdSession.Install` → `ICmdHost.FindLinker`/`InstallLinker` → `LinkerInstaller`이고, `LinkerPlace`(게임 폴더·모드 위치·그 버전·설치된 버전·쓸 폴더)를 먼저 만들어 **질문 문구에 쓸 폴더를 적습니다**.
- **update**: `MainWindow.Console.cs`의 `ScriptRun("update.ps1", …)`이 만든 `powershell -NoProfile -ExecutionPolicy Bypass -File <저장소>\update.ps1`을 띄웁니다(`RunScriptAsync`가 `step <0..1>` 줄은 막대로, 나머지는 줄 그대로 돌려줌). `CmdSession.Update` → `ICmdHost.CheckUpdate(version)`(`-Check -Progress`)의 `state …` 줄을 `UpdateCheck.Read`가 읽고, 영어 문장은 `CmdSession`이 만듭니다(최신·대기·자기 수정·없는 버전·같은 버전). 받을 때는 `Confirm` → `ICmdHost.ApplyUpdate(force, version)`가 `update.ps1 -WaitForProcessId <pid> -Restart [-Force] [-Version v] [-LinkerGame 게임]`을 **출력을 읽지 않고** 띄운 뒤 앱을 닫습니다(실행 중에는 자기 Release 폴더가 잠겨 빌드 불가). 링커가 설치되어 있으면 `-LinkerGame`을 넘겨 스크립트가 `build-mod.ps1 -Install`까지 합니다. `version`도 같은 `CheckUpdate(null)`로 최신 여부를 적습니다. `install -reinstall`은 `ICmdHost.ReinstallLinker`(앱은 그대로)·`ReinstallAndRestart`(앱을 닫음)로 `-Reinstall`을, `scannow source`는 `-Scan`을 씁니다. 저장소 루트는 `WorkspaceMapCache.FindRepositoryRoot`로 찾고, 못 찾으면 그렇다고만 답합니다. **앱이 다른 프로그램을 띄우는 유일한 곳**이며 올리는 쪽은 앱에 없습니다(편집기·git으로 직접 올립니다, 사용자 결정).
- **시작·끝 노트 색**(1.0.0): `NoteField`의 `BrushOf(index, chord, pressed)` 한 곳이 시작(`notes[0]`의 시각) > 끝(`notes[^1]`의 시각) > 체크포인트 > 동타 > 일반 순으로 고르고, 팔레트는 `NoteFieldPalette`의 `Start`·`End`(+ 눌린 색)입니다. 시작 누름이 첫 동타 전체를 치는 것은 `MainViewModel.PressLane(tile, starting)` → `NotePressJudge.PressTogether`이며, `starting`은 창의 키 처리기가 "누르기 전 Waiting, 누른 뒤 Playing"으로 정합니다.
- **note의 출처**(1.0.0): `ICmdHost.NowNotes`가 수정 창이 열려 있으면 `(_editorTimeline, editor.Session.ToLayout())`, 아니면 `(Playback.Timeline, Playback.Layout)`을, `ICmdHost.EditOf(song)`이 id로 고른 곡의 저장된 편집을 줍니다. `CmdSession.PrintNotes`가 규칙 노트를 만들고 `NoteEditLayout.Apply(..., anyTile: true)`로 편집을 덮습니다(화면에 없는 레인도 그대로 찍음).
- **업데이트 소식**(1.0.0): `MainWindow.ShowUpdateNotes`가 `OnLoaded`에서 `OfferLastSong` **앞**에 `ReleaseNotes.ToShow(언어, last-version.txt, AppInfo.Version, RanBefore)`의 버전들을 `ConfirmDialog`(버튼 하나, `Topmost`)로 보여 주고, 먼저 `last-version.txt`를 씁니다.
- **load -force**: `CmdSession.LoadOne`(항상 `Confirm`) → `ICmdHost.ForceLoadSongById`(`MainWindow.Console.cs`) → `MapSyncService.ForceAsync` → `AdofaiTimelineParser.ParseForced`. 캐시 항목의 `ForcedFormatVersion`이 표시이며, `SyncAsync`는 원문 hash가 같으면 다시 강제로 읽어 유지합니다.
- **캐시 저장 범위**: 식별자·경로·곡 이름·원문 hash·파서 버전·상태·경고·원본 hit time·타일 번호·hit 종류·검증된 체크포인트·미니게임 구간·곡 정보·강제 로드 형식. `ParserVersion`이 다르면 Load Song에서 재동기화를 안내합니다.

## 4. 기본값과 노트 규칙 요점

**모든 초기값의 원본은 `src/Visualizer.Core/Settings/DefaultSettings.jsonc`입니다.** 코드 기본값을 직접 바꾸지 말고 이 파일을 고칩니다. 아래는 규칙을 이해하는 데 필요한 값만 적습니다.

- 모드 1·2·4·6·8, 경계 `<50ms` 8K, `<70ms` 6K, `<120ms` 4K, `<500ms` 2K, 그 이상 1K. 최대 타일 수(기본 4)로 상한, 최소 타일 수(기본 1)로 하한. **느린 쪽 모드와 경계값이 같으면 그 모드를 건너뜁니다**(`DynamicTimingSelector.Select`). 화면에 보일 레인 수는 `DisplayLaneCount`(0 = 최대 타일 수를 따름)이고 배치에는 영향이 없습니다(`GetShownTiles`·`GetDisplayTiles(쓰는 타일)`).
- 패턴은 **타일 집합**이라 오름차순으로 저장·비교하고(`SortTiles`), 도는 순서는 `EnableTilePreference`(기본 OFF)가 꺼져 있으면 기본 순서(큰 번호부터)입니다. 1키 패턴 = `InitialTile`(`OneKeyPattern` 없음).
- 동타는 중앙 기준 우선순위로 레인을 찾습니다(`SimultaneousNotePlanner.Assign`의 level): ① 중앙에서 바깥으로 두 칸씩 묶은 쌍 ② 한쪽 반 안의 인접 쌍 ③ 중앙을 가로지르는 쌍(레인 4개에서는 `NoChordCenterPair`를 끌 때만) ④ 여분 레인. 기본 동작은 **왼쪽 쌍 우선**이며 `ChordLanePreference`로 `1-2`/`3-4`/`1-2,3-4`(기본)를 지정합니다 — 선호 쌍은 `GetPreferredChordLanes()`로 오고 `Adjacent`가 레벨 1부터 허용합니다. **트릴은 동타 다음에 계산합니다**(`SimultaneousNotePlanner.SplitTrills`가 `Place`의 마지막에 돌며 둘째 노트만 한 쌍의 다른 레인으로 옮김, 설정은 `TrillWindowMs`·`NoTrillCenterPair`·`TrillLanePreference`). `ChordSectionLanes`는 동타 바로 뒤 단타가 그 동타의 레인을 피하게 합니다.
- 화면에는 최대 키 패턴의 타일만 오름차순 레인으로 표시하고, 여분 레인 배치가 켜진 2·4·6키 화면은 양옆 레인을 더 그립니다(`GetDisplayTiles`). 동타는 `StartsChord`로만 그립니다.
- 마법진(같은 간격 ±5ms가 이어지는 구간)은 `MagicCircleFinder`가 찾고, 고정 타일이 켜져 있으면 그 구간의 키 수를 `MagicCircleTiles`로 고정합니다(값은 최대·최소로 clamp, 설정 값 자체는 유지).
- 실험적 기능은 `ExperimentalFeatures`가 켜져야 하위 항목이 적용되는 구조입니다. 지금 항목은 **동타 앞 단타 레인 분리**(`UseChordAvoidBefore`, `SimultaneousNotePlanner.Place`가 앞 단타를 옮김)와 **연타마다 패턴 다시 시작**(`UseRestartOnRun`, `TilePatternCursor.RestartCycle`)이며 둘 다 기본 꺼짐입니다. 키 수를 뒤 간격으로 정하는 `NextIntervalTiles`는 고급 설정입니다. `UseChordSectionLanes`·`UseChordCenterPair`·`PreferredChordLane` 같은 계산 값은 예전 설정 파일을 읽기 위해 남아 있습니다.
- 고급 설정 밖의 토글은 기본 켜짐이 되도록 이름을 정합니다(사용자 선호, 예: 동타 중앙배치 → 동타 중앙배치 불가).

## 5. 문서 지도

| 문서 | 내용 |
| --- | --- |
| `docs/note-rules.md` | 설정 표, 동적 판정, 사이클, 동타, 레인 키 효과, 판정선/제거선, Reset/Start 명세 |
| `docs/adofai-parser.md` | 파서 지원 범위와 게임 파일 근거, 맵 검증표 |
| `docs/checkpoints.md` | 체크포인트 근거·경계, 자동 진행, 연습 구간 시작 |
| `docs/environment-compatibility.md` | 버전·시작 대기·배속·일시정지·GPU 배치 |
| `docs/windows-input.md` | Raw Input 관찰, 레인 키, 전경 확인 |
| `docs/timing-comparison.md` | 독립 기준 계산 대조, 실제 플레이 확인 절차 |
| `docs/console.md` | 콘솔 명령·옵션·키워드·설정 이름(`구역.설정`)의 원본 |
| `docs/architecture.md` | 계층·확장 지점·저장소 설계 (배속·대기 흐름은 코드 기준) |
| `docs/long-notes-plan.md` | 롱노트·자동 타일 근거와 설계(구현됨) |
| `docs/map-storage-plan.md` | 맵 폴더 구조·곡 수정 설계 원문(구현됨; 문서 제목의 "미구현"은 작성 당시 표기) |
| `docs/song-list-plan.md` | 곡 리스트 요구사항·결정 기록(구현됨; 보관소 절은 제거된 기능) |
| `docs/game-sync-plan.md` | 게임 시작 동기화: 읽는 값 5개와 근거, 파이프 형식, 시계 정렬, 카운트다운 선재생, 단계 |
| `docs/report-editor-plan.md` | 콘솔 report의 vim 같은 편집기 층: 화면·모드·명령과 결정 기록(구현됨) |
| `docs/future-plans.md` | 게임 모드 연동·인스톨러 등 남은 계획 답변 (노트 직접 수정은 곡 수정으로 구현됨) |
| `docs/handoff-history.md` | 승인 범위 원문과 버전별 인계 기록 (**평소에는 읽지 않음**) |
| `docs/demo-guide.md` | 시연 절차 |
| `docs/debug-logs/` | **곡이 Unsupported이거나 타이밍이 어긋날 때의 진단 절차와 실제 해결 예시**(다음 에이전트용). 새 사례는 각 문서의 "해결 기록" 절에 날짜순으로 추가 |

기능·규칙을 바꾸면 해당 문서와 AGENTS.md의 "현재 상태"를 함께 갱신하고, 경위는 `docs/handoff-history.md` 맨 위에 추가합니다.

## 6. 코드 작성 관례

- 모든 시간은 **ms `double`**이며 `double.IsFinite`·음수·비감소 검사를 거칩니다. 두께·배속·판정선 높이(px)는 ms가 아닙니다.
- 설정은 불변 `record`로 `with` → `EnsureValid()`. 공개 메서드는 잘못된 입력에 `ArgumentException` 계열을 던집니다.
- **주석**: 영어로 짧게, 코드만 봐서는 알 수 없는 **이유**를 적습니다. 버전·날짜·요청 이력은 적지 않습니다(이력은 `docs/handoff-history.md`). 사용자가 정한 규칙이라 함부로 바꾸면 안 되는 곳은 `(user rule)`로 표시하고, 예전 파일과의 호환 설명에만 버전을 적습니다. 게임 파일 근거(클래스·메서드 이름)와 근거가 된 맵 이름은 남깁니다.
- **토글 옆 상태 문구**: `AddField`가 `CheckBox`를 받으면 `WithState`로 작은 회색 문구를 붙입니다. 모든 토글이 `StateWords`에 자기 쌍을 갖고(켜짐/꺼짐은 대비용), 새 토글도 쌍을 추가해야 `AdvancedFeatureTests`를 통과합니다. `InputFor`는 그 묶음에서 토글을 꺼내 돌려줍니다.
- **설정 창은 새 스냅샷을 만듭니다**: `BuildSettings()`에 없는 속성은 적용할 때마다 조용히 기본값으로 돌아갑니다. 화면에 행이 없는 설정도 값을 실어 날라야 합니다(`SongListFields`처럼). `SettingsStoreTests`의 전 속성 대조가 이 실수를 잡습니다.
- **콤보박스 항목은 언어 적용 때 채워집니다**: `LoadForm`이 먼저 돌기 때문에 `SelectedIndex`만 믿지 말고 `UpdateLanguage`에서 `_current` 값으로 되돌릴 수 있게 둡니다.
- **새 콘솔 명령·옵션 추가 절차**: ① 옵션인지 키워드인지를 `docs/console.md` 맨 앞 "옵션과 키워드의 원칙"으로 먼저 가립니다(부사면 옵션, 목적어·동사면 키워드) ② 옵션은 **낱말 이름**으로 `CmdCatalog.Options`에 넣고 짧은 별칭은 `Short`에, 두 낱말이 필요하면 **`이름:값`**(`Values`·`Default`, **기본값 필수**)으로 ③ 명령의 `Options` 문자열에는 **긴 이름만** 적고, `Help`·`Summary`·키워드 뜻에도 **짧은 별칭을 쓰지 않습니다** ④ 키워드는 `Keywords`에 적고 뜻을 `KeywordText`(명령마다 다르면 `"명령 키워드"`)에. 인자 없이 쳤을 때의 뜻이 키워드 하나와 같으면 그 낱말에 **`*`**를 붙입니다(`"list*, last"` → 도움말이 `[list]`로 표시). `list`는 나열, `all`은 대상 전부입니다 ⑤ **키워드가 필수인 명령**(사용법이 `<what>`·`<action>`이나 키워드 자체)은 자기 키워드와 같은 이름의 옵션을 두지 않고, 키워드 없이 오면 받는 낱말을 알려 주고 멈춥니다 ⑥ `docs/console.md`의 표에 줄 추가 ⑦ 동작은 `CmdSession`의 `switch`에(수정 창을 건드리는 명령이면 `NotWhileEditing`에도). 파서는 옵션을 키워드·인자 **앞뒤 모두**에서 읽으며(`CmdCatalog.Parse`, 자유 글자 인자만 앞에서), `report`처럼 인자가 자유 글자인데 낱말이 앞에 오는 명령은 스스로 걸러 냅니다(`CmdSession.OpenReport`). 규칙을 어기면 `CmdConsoleTests`의 "Options and keywords keep to their rules"가 빌드를 막습니다.
- **새 설정 추가 절차**: ① `VisualizerSettings`에 속성(초기값은 `DefaultSettings.Text/Number/Integer/Flag(nameof(...))`) ② `DefaultSettings.jsonc`에 같은 이름으로 초기값 ③ `Validate`/`ValidateFields`에 검증 ④ 노트 배치에 영향을 주면 `HasSameNoteRulesAs`에 비교 ⑤ `SettingsPanel`의 섹션과 `BuildSettings`·`LoadForm`에 입력 ⑥ **모든 언어 파일**에 라벨·`Hint.<설정이름>`·`?` 설명 키, 토글이면 `StateWords` 쌍 ⑦ 다른 설정 때문에 잠기는 입력이면 잠긴 동안에도 값을 저장·복원(콘솔 잠금은 `SettingPaths.LockedBy`) ⑧ 콘솔 이름을 `SettingPaths`에 추가(없으면 `CmdConsoleTests` 실패)하고 `docs/console.md` 표에 적기. 속성 목록과 jsonc가 다르면 `LocalizationThemeTests`가 실패합니다. 설정 이름을 바꾸면 예전 키를 `SettingsJson`이 읽도록 둡니다(`ReadLegacyChordRules` 참고).
- 사용자에게 보이는 문자열은 **언어 파일 키로만** 추가하고 코드에 문장을 직접 쓰지 않습니다(Core 검증·JSON·배치 실패 메시지 포함). 예외: 콘솔(`CmdSession`·`CmdCatalog`·`CmdWindow`)은 언어와 무관하게 영어 고정입니다. 파서 경고와 `ParsedTimeline` 예외 문구는 아직 한국어 고정입니다.
- 한국어 UI 문구는 "켜면/끄면" 대신 "활성화하면/비활성화하면"으로 씁니다. `Hint.*`는 "~니다." 한 문장입니다.
- 색은 XAML·스타일에서 `{DynamicResource …Brush}`로만 참조하고 새 색은 `ThemeCatalog.BrushKeys`와 **모든 테마 파일**에 추가합니다(다른 색에서 계산하는 `PrimaryBrush`·`ToggleOnBrush`는 `ThemeCatalog.Create`에서 만듦; 스타일 참조와 `IconFont`만 `StaticResource`). 노트 영역 색은 테마와 무관하게 설정에서 옵니다.
- 버튼·입력은 `Themes/Theme.xaml`의 키(`ToolButton`·`PrimaryButton`·`DangerButton`·`IconButton`·`PillToggle`·`StatePill`·`CompactToggle`)를 쓰고, 재생 표면은 `PlaybackButton`/`PlaybackToggleButton`을 씁니다.
- ViewModel 명령은 필요한 속성만 알립니다. `NotifyAll`은 곡 불러오기·동기화·설정 적용처럼 드문 작업에만 씁니다. `CheckpointOptions`는 타임라인·언어별로 캐시합니다. 바인딩이 값을 넘겨준 setter는 같은 속성을 다시 알리지 않습니다(입력 중인 칸에 값이 되돌아감).
- `catch`는 예상 예외 타입만 필터링합니다. `NotePlacementException`은 ViewModel이 잡아 메시지로 표시하고, 창 프로시저 안의 키 처리는 예상 예외를 모두 잡습니다.
- `DllImport` 네이티브 호출은 Infrastructure에만 두고 `[SupportedOSPlatform("windows")]`를 붙입니다.
- 파서는 미지원 조합을 `Unsupported`, 잘못된 JSON·필드를 `Error`로 반환하고, 캐시 내용이 달라지는 변경에는 `CurrentVersion`을 올립니다.
- 렌더 경로에서는 브러시·펜을 Freeze하고 보이는 노트 구간만 이진 탐색으로 순회합니다. 렌더링을 바꾸면 이전 렌더러와 번갈아 여러 번 프레임 간격을 비교합니다(이 PC는 같은 코드도 8~12ms로 흔들립니다).

## 7. 테스트 하네스

`tests/Visualizer.Tests/Program.cs`의 `Main`이 `(string Name, Action Run)` 목록을 실행하고 `N/M test groups passed`를 출력합니다. 인자는 artifact 절대 경로, 선택 인자는 `--read-only-map-root PATH`·`--compare-game-rules`입니다. 새 테스트는 해당 파일의 `Create(...)`에 추가하고, 새 파일은 `Program.cs`의 `.Concat(...)` 체인에 등록합니다. 헬퍼는 `Program.Assert/Equal/Sequence/Throws/Capture`입니다.

파일별 담당: `Program.cs`(기본·노트 규칙 회귀) · `ExtendedTests` · `PlumExampleTests` · `SimultaneousNoteTests`(동타) · `SixKeyTests`(6키) · `EnvironmentCompatibilityTests` · `CheckpointControlTests` · `CheckpointLayoutTests`(좁은 창) · `RhythmVisualTests`(레인 키·판정·캡처) · `PracticeSectionTests`(구간 시작) · `AdvancedFeatureTests`(설정 창·메인 화면·검색창 키) · `ExtraLaneTests` · `LocalizationThemeTests`(언어·테마·곡 정보) · `DesignAndTimingTests`(마법진·너비·투명도·동타 규칙) · `LongNoteTests` · `SettingsStoreTests`(설정 저장·저장 폴더·JSON) · `SongListTests`(곡 리스트·정렬·id·불러오기·배속 초기화·상세 창 검색과 곡 제거) · `NoteEditTests`(곡 수정) · `CmdConsoleTests`(콘솔·Ctrl+T) · `GameSyncTests`(게임 동기화 링크·파이프·링커 설치) · `MapCompatibilityTests`(형식 호환, 외부 맵 감사). `GameRuleTimingComparison`은 그룹이 아니라 `-CompareGameRules`용 독립 기준 계산기입니다.

자주 걸리는 함정:

- 설정 테스트에서 콤보박스를 순서로 찾지 않습니다(오른쪽 위 `ThemePicker`가 먼저). 최대·최소 타일 수는 `ItemsSource is int[]`, 입력은 `SettingsPanel.InputFor(설정이름)`으로 찾습니다.
- 설정은 `new SettingsPanel(settings, new MemorySettingsLayoutStore())`로 열림 상태를 격리하고, 섹션 상태는 `SectionState("SectionTiming")`, 도움말은 "?" 배지의 `ToolTip`으로 확인합니다. 행은 카드(`Border`) 안에 있으므로 라벨은 논리 트리 전체를 검색합니다. 패널은 창이 아니므로 `Capture`의 `FrameworkElement` 오버로드가 받고, 메인 창 안에서 시험하려면 `SettingsButton`을 눌러 `SettingsHost.Child`를 꺼냅니다.
- 순서가 중요한 테스트는 `EnableTilePreference = true`와 선호도를 함께 지정합니다.
- 설정을 바꾼 뒤 캡처할 때는 `field.Tick()`을 호출합니다(앱은 매 프레임 호출하지만 테스트는 그냥 두면 이전 그림이 남습니다).
- 저장소를 건드리는 테스트는 `AppStorage.At(<테스트 폴더>)`를 씁니다. `AppStorage.Open`은 실제 저장소(`%LOCALAPPDATA%\AdofaiVisualizer`)를 가리키고, 저장소 안의 옛 `.local/app-data`를 그리로 옮기기까지 하므로 테스트에서 부르면 안 됩니다. 창의 검사(scannow)를 시험할 때는 `MainWindow.StoreForTests = AppStorage.At(<테스트 폴더>)`로 돌리고 `finally`에서 비웁니다.
- 색이 정해진 노트를 픽셀로 보는 테스트는 **첫 노트가 시작 노트 색**이라는 것을 기억합니다(1.0.0). 일반 노트 색을 보려면 두 번째 이후 노트를 보거나 `StartNoteColor`를 노트 색으로 둡니다.
- 기본값에 기대는 테스트는 값을 직접 지정합니다(경계값·투명도·마법진 고정 타일 등에서 실제로 깨진 적이 있습니다).
- 창을 실제로 띄우는 테스트는 `Left = -20000, Top = -20000, ShowActivated = false`로 화면 밖·포커스 없이 띄웁니다(사용자의 포커스를 빼앗지 않음). 포커스가 없으므로 검색창의 `ShowSongTitleStart`가 커서를 맨 앞으로 옮깁니다. 키 이벤트는 실제 순서(PreviewKeyDown → 처리 안 됐으면 KeyDown → KeyUp)로 보내고, Ctrl 같은 수정 키는 실제 키보드 상태라 테스트에서 누를 수 없으므로 그 키가 부르는 메서드를 직접 부릅니다.
- 결함을 고치는 테스트는 고치기 전 코드에서 실패하는 것을 확인합니다. 임시로 코드를 되돌릴 때는 Python으로 고치고 되돌려 수정 시각을 갱신합니다(1절 파일 편집).
