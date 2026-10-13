# CLAUDE.md

ADOFAI(얼불춤) 커스텀 맵을 Falling Note로 보여주는 개인용 Windows 연습 Visualizer. C# / .NET 9 / WPF, 외부 NuGet 없음.

**작업 규칙과 현재 동작의 원본은 [AGENTS.md](AGENTS.md)이며 충돌하면 AGENTS.md를 따릅니다.** 이 문서는 코드를 찾고 고치는 데 필요한 지도만 담습니다.

**어떤 요청이든 먼저 `.\report.ps1 open`**: open 메모가 있으면 고칠지·모호한 점을 질문한 뒤 고칩니다(사용자 규칙, AGENTS "사용자 메모를 먼저 봅니다").

토큰 절약: 큰 파일(`note-rules.md`, `adofai-parser.md`, `console.md`, `VisualizerSettings.cs`, `NoteField.cs`, `SettingsPanel.*`, `AdofaiTimelineParser.cs`, `CmdSession.cs`, `MainWindow.xaml.cs`)은 통째로 읽지 말고 검색으로 필요한 구간만 봅니다. `docs/handoff-history.md`는 사용자가 경위를 물을 때만 엽니다.

@AGENTS.md

---

## 1. 빌드·테스트·파일 편집

**`dotnet build`를 직접 실행하지 말고 `build.ps1`을 씁니다.** DOTNET_CLI_HOME·NuGet 캐시·TEMP·APPDATA를 프로젝트 내부 `.local/`로 돌린 뒤 복원합니다(IDE 자동 빌드도 밖에 캐시를 씀).

```powershell
.\build.ps1                                   # Debug
.\build.ps1 -Configuration Release            # Release
.\build.ps1 -Test                             # 빌드 + 회귀 테스트 (외부 맵 없음)
.\build.ps1 -Test -ReadOnlyMapRoot '<맵 루트>' [-CompareGameRules]   # 사용자가 요청할 때만
.\build-mod.ps1 -Configuration Release        # 게임 모드 (게임 폴더 필요), plugins/linker에도 씀
.\build-mod.ps1 -Configuration Release -Install   # + <게임>\Mods\AdofaiVisualizerSync\ (요청할 때만)
```

- 모드는 `Visualizer.sln`에 없습니다(게임 없는 PC에서도 `build.ps1`이 돌아야 함). 빌드된 모드는 `plugins/linker/`로 커밋하고, 앱 csproj가 `plugins/**`를 exe 옆으로 복사합니다. `.gitattributes`가 `plugins/**`를 바이너리, `*.cmd`를 CRLF로 둡니다.
- SDK는 `global.json`(9.0.305 이상의 9.0, `latestFeature`), `NuGet.Config`는 오프라인. `Directory.Build.props`에서 `Nullable`·`ImplicitUsings`·**`TreatWarningsAsErrors`**·`Deterministic`이 켜져 있어 경고 하나도 실패입니다. `<Version>`·`<VersionDate>`도 여기.
- 실행 파일: `src/Visualizer.App/bin/{Debug|Release}/net9.0-windows/Visualizer.App.exe`. 사용자가 앱을 실행 중이면 Release 폴더가 잠깁니다 — **앱을 종료하지 말고** Debug로 검증한 뒤 알립니다.
- `build.ps1 -Test`는 첫 실패에서 멈춥니다. 전체를 보려면 같은 환경 변수로 `tests/Visualizer.Tests/bin/Debug/net9.0-windows/Visualizer.Tests.dll <산출물 폴더>`를 직접 실행해 `FAIL` 줄을 봅니다.
- **파일 편집**: Edit/Write 또는 Python(`encoding='utf-8', newline=''`, CRLF 유지). PowerShell 5.1의 `Get-Content`/`Set-Content`는 한글을 깨뜨리고 `Copy-Item`은 수정 시각까지 복사해 증분 빌드가 건너뜁니다. 스크래치 스크립트 이름을 표준 모듈 이름(`json.py` 등)으로 짓지 않습니다.
- 아이콘 글리프(U+E7xx)·제어 문자를 C# 문자열에 그대로 넣지 않습니다(`char.ConvertFromUtf32(0xE768)`, XAML `&#xE768;`).

## 2. 솔루션 구조

```text
App ──► Application ──► Core        Infrastructure ──► Application        Tests ──► App
```

```text
src/Visualizer.Core/
  Maps/            ParsedTimeline(불변 hit time·체크포인트·HitKind·Sections), RawMapDocument
  Settings/        VisualizerSettings(불변 record, Validate/EnsureValid/HasSameNoteRulesAs), DefaultSettings.jsonc(+.cs, 모든 초기값),
                   SettingsJson(내보내기·부분 병합 가져오기·예전 키·Retired), SettingsFields(이름으로 읽기·쓰기·Restore), ColorText
  Localization/    Languages.cs(+ ConsoleKeys·ConsoleGet) + Languages/*.json(파일 이름 = id, "strings" 창 문구 + "console" 콘솔 문구), ReleaseNotes,
                   ConsoleText(콘솔 언어 Language·T, 세션 Speak와 MainWindow.SpeakConsole이 앱 언어로 맞춤)
  Visualization/   DynamicTimingSelector(간격→모드) · TilePatternCursor(전환·회전·트릴) · NoteGenerator(묶음→모드→커서→배치)
                   SimultaneousNotePlanner(동타·트릴 배치, NotePlacementException) · MagicCircleFinder · NoteLayout · NotePosition
src/Visualizer.Application/
  Maps/            IReadOnlyMapSource, IMapTimelineParser(ParseForced), MapSyncService(동기화·ForceAsync), MapFileRules
  Cache/           IMapCache, MapCacheEntry
  Library/         SongPreference·ISongPreferenceStore, SongOrdering, MapKind, SongIds, NoteEdit·NoteEditSession·NoteEditLayout
  Sync/            GameSyncLink(모드 값 검사·측정) · GameTileCheck(타일 대조) · GameSyncFollower(재생 지점만 이동)
  Playback/        PlaybackSession(Off/NoTimeline/Waiting/Playing/Paused) · NotePressJudge(시각 전용 판정) · RulePreviewTimeline
  Input/           InputPolicy, IInputMonitor, KeyPressTracker
src/Visualizer.Infrastructure/
  Maps/ReadOnlyDirectoryMapSource      .adofai 열거·읽기만, 루트 탈출·reparse point 거부
  Cache/WorkspaceMapCache              <저장소>/map-cache/library.json, tmp → 원자 교체, FindRepositoryRoot
  Storage/AppStorage                   저장소 결정(%LOCALAPPDATA%\AdofaiVisualizer · 지정 폴더), settings.json, data-root.txt, last-version.txt
  Storage/StoreMove                    옛 .local/app-data를 공유 저장소로 한 번 복사하며 곡 id(경로 해시)를 새 경로로 다시 씀
  Storage/MapLibrary                   maps/{base,custom,edited,removed}
  Storage/NoteEditStore                maps/edited/<곡>/<슬롯>/{원본.adofai, edit.json}
  Storage/WorkspaceSongPreferences     songs.json      Storage/SongIdBook  song-ids.json      Storage/SongMarkBook  song-marks.json
  Storage/PinBook                      pins.json (곡 묶음 CmdSong.Family 키별 타일)
  Storage/ReportBook                   reports/*.txt (모든 .txt가 메모, 상태는 written.mark·done.mark). 터미널 쪽은 report.ps1
  Parsing/AdofaiTimelineParser         CurrentVersion·FormatVersions·허용 이벤트·ParseForced (근거 docs/adofai-parser.md)
  Parsing/AdofaiJson                   문자열 안 제어 문자·생략된 쉼표를 메모리에서만 보정
  Input/                               WindowsRawInputMonitor(관찰 전용), WindowsForegroundObserver(+ForegroundProcessId), MultimediaScheduling(MMCSS),
                                       KeyboardInjector(link inspect 전용 SendInput, 안전 규칙 6 예외)
  Sync/NamedPipeGameSyncServer         모드의 명명 파이프를 읽기만
  Sync/LinkerInstaller                 게임 폴더·모드 파일 찾기(plugins\linker 먼저), UMM 여부, 두 파일만 쓰기
src/Visualizer.App/
  Icon/            icon.png(원본) → icon.ico(csproj ApplicationIcon)
  Themes/          Theme.xaml(공용 스타일), ThemeCatalog(+ PrimaryBrush·ToggleOnBrush 계산) + Palettes/*.json, SpaceBackdrop
  Views/           MainWindow(서비스 조립·Raw Input 훅·프레임 루프·검색창 키) · .Console.cs(ICmdHost) · .Scan.cs(scannow·reinstall) · .Inspect.cs(link inspect)
                   SettingsPanel(UserControl, .Rows 행 만들기 · .Form 읽기·쓰기 · .Json) + SettingsLayoutStore
                   SongListWindow · SongDetails · NoteEditWindow(+ EditSlotDialog, EditKeysWindow, 도구 패널 BuildPanel)
                   CmdWindow(터미널 화면, ReportEditor 층) · HelpWindow · ConfirmDialog(risky = 주황 아웃라인)
                   테스트 스위치: MainWindow.UseAppStore · StoreForTests · PlaybackClock · GameSyncPipeName
  Commands/        CmdCatalog(명령·옵션·키워드·파서) · CmdSession(동작·[Y/n]·Tab·copy/prompt/report) · SettingPaths(set/get 이름·잠금)
  ViewModels/      MainViewModel(설정·이력·재생) .Songs(목록·검색·불러오기) .Placement(배치 실패 제안) .Overlay(곡 정보)
  Rendering/       NoteField(Tick, BrushOf) · NoteFieldPalette · NoteEditField(RuleNotes) · FrameStats
  Input/           ControlBindings, KeyNames, KeyCaptureBox, PlaybackButton, LinkInspection(+InspectRun·InspectPress)
  Localization/    UiText(XAML Text[key], 코드 Format)
tests/Visualizer.Tests/   실행형 하네스 (xUnit 없음)
tools/GameSyncMod/        읽기 전용 모드(net48, UMM): Main · GameReader(리플렉션) · KeyViewerFocus · SyncWriter. Info.json은 빌드가 만듦
plugins/linker/           빌드된 링커(커밋함)
update.ps1                저장소 쪽 일의 유일한 스크립트: -Check · 받기 · -Force -Version · -Reinstall · -Scan · -LinkerGame · 다시 띄우기
Install.cmd / Uninstall.cmd / uninstall.ps1   더블클릭 설치·제거 (AGENTS 안전 규칙 2)
report.ps1                메모 list·open·read·state·done·undone·asked
.example/main.adofai      읽기 전용 예제 (Plum - R)
.local/                   도구 홈·캐시·테스트 산출물·백업 (gitignore)
```

## 3. 데이터 흐름과 코드 위치

```text
시작      AppStorage.Open → settings.json → SettingsJson.Import (없거나 깨졌으면 기본값 + 안내) → ForgetSettingsHistory
적용      설정 변경 → MainViewModel.SettingsHistory → SettingsCommitted → MainWindow(바인딩 갱신 + 저장)
Map Sync  ReadOnlyDirectoryMapSource → AdofaiTimelineParser → MapSyncService → WorkspaceMapCache
Load Song 캐시 항목 → LoadAsked(배속 초기화) → LoadEntry → PlaybackSession.LoadParsedTimeline (재파싱 없음)
Reset     ① 체크포인트 첫 hit 기준 정규화 ② 배속으로 나눔 ③ NoteGenerator ④ StartDelayMs 더함
          실패(NotePlacementException) → Notes 비움, Start 차단
Start     시계 기준점만 기록 (Paused는 같은 지점에서 재개)
프레임    CompositionTarget.Rendering → MainViewModel.RefreshPlaybackInfo() → NoteField.Tick()
키 입력   Raw Input → KeyPressTracker → ControlBindings → InputPolicy → 명령 / (레인) MainViewModel.PressLane
```

- **판정선**: progress = `1 - (hitMs + globalOffset + mapOffset - elapsedMs) / travelTimeMs`(0 상단, 1 판정선). 판정선은 제거선보다 `JudgmentLineHeightDip` 위(창이 작으면 노트 영역의 60%까지).
- **체크포인트 자동 적용**: 그 타일 노트를 누른 순간, 안 누르면 입력 인정 범위가 끝날 때. `AutoCheckpointAdvance` 꺼짐·배속≠100%·구간 시작이면 유지·잠금. 수동 선택은 즉시 정지 배치, 목록 양 끝에서 순환.
- **ApplySettings**: `HasSameNoteRulesAs`가 다르면 노트를 다시 준비하고, 그 밖의 설정은 노트·시계·일시정지 상태를 유지합니다. `RollBack`은 마지막 단계의 바뀐 항목만 `SettingsFields.Restore`.
- **시작 위치**: 구간이나 두 번째 이후 체크포인트는 곡 처음부터 만든 배치를 **잘라 쓰고** 첫 노트 레인 키로 시작합니다. 그래서 곡 앞부분 배치 실패가 뒤 체크포인트도 막습니다.
- **입력 정책**: 전역 입력 범위를 켜면 ADOFAI 또는 이 앱이 전경일 때만. UI 입력 편집 중·설정판 열림·콘솔 활성이면 반응하지 않음. 레인 키는 시작 키와만 겹칠 수 있고 Start가 먼저. 일시정지 중 레인 키는 ±1초 안의 그 레인 노트로 재개.
- **일시정지 정렬**: `MainViewModel.AlignPausedNote` → `NotePressJudge.FirstUnpressed` → `PlaybackSession.MovePaused`(`PauseFromKey`·`PauseResume`, 설정 `AlignNextNoteOnPause`).
- **곡 끝 알림**: `PlaybackSession.PastLastNoteMs` + `MainViewModel._lastInputMs`(`NoteInput()`)로 `RefreshPlaybackInfo`가 `ShowSongEndNotice`(`SongEndQuietMs`). 초기화 키는 `ResetFromKey(offered)` — 창이 `NoteInput()` **전에** `offered`를 읽어 넘깁니다. 버튼 `RestartFromBeginning`·`RestartAtLastCheckpoint` → `StartOver`. XAML `SongEndOffer`(띠 `SongEndCard`), 두 번째 버튼이 보이면 띠 위의 보이지 않는 쌍둥이 `SongEndBalance`도 자리를 잡아 첫 버튼 높이가 그대로입니다.
- **노트 영역 위**: `OverlayArea`(한 칸 Grid) 안에 `OverlayBand`(`SongOverlayCard`·`FieldControls`)와 `HideOverlayButton`이 층으로 있습니다. `ShowOverlayBand = !SongOverlayHidden && (ShowSongOverlay || ShowFieldControls)`. 배경은 `OverlayBackBrush`. 마지막 줄은 `OverlayTileParts` → `Views/OverlayLine.cs`가 바뀐 조각에만 밑줄. `SectionNoticeCard`는 위쪽 별도 띠(`PlaybackSession.SectionAt` → `SectionNotice`). **이런 `Show…` 속성은 `MainViewModel.PlaybackProperties`에 이름이 있어야 재생 상태가 바뀔 때 화면이 따라옵니다.**
- **조작 줄**: 배속 `SlowerButton`·`SpeedBox`·`FasterButton` → `StepSpeed`/`ApplyTypedSpeed`/`ApplySpeed`(`SpeedText`는 `%`까지 붙임) · `SectionField` · 슬롯 `SlotOptions`/`SelectedSlot`(setter가 `LoadCached`). 치는 동안 값이 되돌아가지 않도록 setter에서 알리지 않습니다.
- **설정판**: `SettingsPanel`은 `MainWindow.xaml`의 `SettingsHost` 안 `UserControl`. 폭은 창 본문(`WindowBody`) 기준. 창이 하던 일: `Leaving` 콜백(→ `OpenSettings`), `TryLeave`, 패널의 Escape, `Window.GetWindow(this)`. 미리 만들기 `PrepareSettings`(`ApplicationIdle`)·`TakeSettings`, 쓴 패널은 재사용하지 않음. 검색 `Search(text)`(`_sections`·`Words`, `_searching` 동안 접힘 상태 저장 건너뜀). 미리보기 `ShowPreview` → `Previewing` → `MainWindow.ShowSettingsPreview`(`RememberLoaded`·`LoadPreview`) / `EndSettingsPreview`. `SizeControls`가 높이·글씨를 한 번에 정합니다.
- **메인 창 입력 차단**: `MainWindow.Busy`(설정판 `_dialogOpen` 또는 수정 창)면 `PreviewMouseDown`·검색창 키를 막되 `SettingsHost`·`PreviewBar` 안은 예외. 콘솔은 `ICmdHost.EditorOpen` → `CmdSession.NotWhileEditing`.
- **단축키**: Ctrl+E `MainWindow.IsEditShortcut(e)`(메인 `PreviewKeyDown`은 `Busy`면 넘김 → 설정판 쪽 `EditSong()`). Ctrl+T는 Raw Input의 `ObservedConsoleKey`가 처리(`InputPolicy.ShouldOpenConsole`: 앱 전경이면 언제나, 아니면 전역 입력 범위)하고, 창들(`MainWindow`·설정판·`NoteEditWindow.ConsoleShortcut`·`CmdWindow.Shortcut`·`ReportEditor`)은 `ConsoleKeyInWindow`로 삼키기만 합니다(관찰 불가일 때만 토글). 키 이벤트의 실제 키는 `KeyNames.Real`(한글 IME의 `ImeProcessed`).
- **검색창**: `SongQuery`(보이는 글자) / `_filter`(친 글자), `SongToLoad`, `PickInSearch`. 편집 가능한 콤보박스가 Enter·방향키를 Preview 클래스 처리기에서 먼저 먹으므로 메인 창 `PreviewKeyDown`이 `SongBoxKey`로 먼저 받습니다. `OpenSongList`는 전체 선택된 커서를 되돌리고, 포커스가 없으면 `ShowSongTitleStart`.
- **곡 목록**: `RebuildLibrary`가 숨긴 곡을 빼고 `DisplayName`을 덮어 `SongOrdering.Sort`(전체 그룹이면 `SongFamilies`로 원곡 아래 수정본). `AllSongs`는 상세 창용. 배지 `MapCacheEntry.Badge` ← `BadgeOf`(forced → recently → new). `new`는 `CopyAndScanAsync` → `MarkNew`, `recently`는 `SaveEdit` → `ScanEditedAsync` → `MarkRecentEdit`.
- **곡 리스트 상세 창**: `SongListFields` 순서로 한 줄 행(`RowHeight`). 수정본 행은 `Row.Follows`, `MoveRow(from, gap)`·`Snap`, 드롭 위치 `GapAt`. 검색 `_filter`·`Matches`(손잡이 잠금). 제거 `RemoveSong` → `AskRemove` → `MainWindow.RemoveListed`(수정본 `Maps.Hold`, 나머지 `Maps.Remove` + `Forget`), 테스트는 `RemoveRow(index)`.
- **불러오기**: 사용자가 부른 것(`LoadSelectedSong`·`MoveSong`·`LoadExample`·`LoadChosen`)은 `LoadAsked`(배속 초기화 `KeepSpeedOnLoad`), 복원은 `LoadCached`. 마지막 곡 `LastSongId` → `OnLoaded` → `ShowUpdateNotes`(`ReleaseNotes.ToShow`) → `OfferLastSong`(`SongDetails.Lines`).
- **배치 실패 제안**(`MainViewModel.Placement`): 원곡은 `ConfirmExtraLanes` 또는 `ConfirmWiderLanes`, 수정본(`Playback.Layout`)은 `EditedLanesQuestion`. 곡·설정당 한 번.
- **곡 수정**: `NoteEditSession`(선택·이동·`CanMerge`·분해·`Flip`·되돌리기·`Loosened`), 저장 `NoteEditStore`(`SlotsOf`·`SongFolderOf`), 재생 반영 `PlaybackSession.Layout` → `NoteEditLayout.Apply`. 편집은 레인 위치 기준(`NoteEditSession.Lanes`), 창은 1~8(`MainWindow.AllLanes`). 키 `NoteEditWindow.Press`, 휠 `Wheel`, 도구 패널 `BuildPanel`(`Tools`·`ZoomText`·`ZoomBy`·`ResetZoom`·`ZoomAround`), `Confirm`은 테스트용 대체. 다시 열 때 위치는 `MainWindow._editorPlaces`(곡 묶음 키, 메모리만) → 생성자 `topMs`, 저장 뒤 다시 불러오기는 `MainViewModel.Reload`(구간 유지).
- **맵 폴더**: 캐시 항목의 `RootDirectory`가 종류(`MapLibrary.KindOf`). 동기화는 스팀 → custom 복사 후 세 폴더 파싱, 앱 폴더 밖 항목 정리(`DropForeignEntriesAsync`). 곡 id는 `SongIds.Assign`(`AssignIds`).
- **시작·끝 노트 색**: `NoteField.BrushOf(index, chord, pressed)`(시작 > 끝 > 체크포인트 > 동타 > 일반, 시작·끝은 눌려도 같은 색), 팔레트 `NoteFieldPalette.Start`·`End`. 시작 누름의 동타 전체 처리는 `PressLane(tile, starting)` → `NotePressJudge.PressTogether`.
- **콘솔 진행률**: `CmdSession.Job`·`Busy`·`JobText()`·`RunJob`(호스트에 `IProgress<double>`과 끝 콜백), `Submit`은 `Busy`면 버림. 호스트 `SyncMaps`/`Resync`/`InstallLinker`. 진행률은 `MainWindow.CopyAndScanAsync`의 `Step`(복사 40% + 파싱 60%).
- **핀·go**: `CmdSession.Pin(line, copy, add)` · `TileProblem`(go·pin 공통) · `PinWords`(Tab) → `ICmdHost.PinsOf`/`SetPins` → `MainWindow.Console.cs`의 `Pins`(읽기 실패 시 `_pinsUnreadable`로 쓰기 막음) → `PinBook`. Tab 위치는 `CmdArgument.Tile`·`Pin`.
- **note**: `ICmdHost.NowNotes`(수정 창이 열려 있으면 그 레인) · `EditOf(song)` → `CmdSession.PrintNotes` → `NoteEditLayout.Apply(..., anyTile: true)`.
- **load -force**: `CmdSession.LoadOne`(항상 `Confirm`) → `ForceLoadSongById` → `MapSyncService.ForceAsync` → `ParseForced`. 표시는 캐시의 `ForcedFormatVersion`.
- **link**: `link auto`는 `_linkAuto`·`AutoFollowGame`이 프레임마다 `FollowGameSync` 맨 앞에서, 게임 곡이 불러온 곡과 다르면 `LoadGameSong`(`SongFor`가 원곡 우선, `_autoLoadedPath`로 게임 곡마다 한 번, `Busy`·설정판이면 대기)을, 같으면 `ICmdHost.GameSyncFollow(true)`를 부릅니다(`_autoTried` 열쇠는 레벨 객체·곡·`PlaybackSession.Prepared`, 거절 뒤 `AutoRetryMs`마다 다시, 이유는 `_autoNote`, 메인 화면 메시지는 열쇠가 바뀐 시도에만 `GameAutoFollowing`·`GameAutoRefused`·`GameAutoStaleCache`(`MainViewModel.IsStale`); 곡 비교 `GamePlays`는 수정본을 원곡 경로로). 켜져 있는 동안 `HandleObservedKey`가 `InputPolicy.MovesPlayhead` 동작과 레인 키 시작·재개를 건너뜁니다. 따라가기 스위치는 `MainViewModel.FollowingGame`, 기준은 `PlaybackSession.ClockFollowsGame`, 상태 알림 `NotifyPlaybackChanged()`. 노트 세우기는 `GameSyncFollower.Stand`(끊김·다른 레벨·게임 오버·속도)와 `StillMs`(일시정지·멈춤), 재연결은 `GameSyncLink.BeginConnection`, 모드의 일시 오류는 `GameReader.Trouble`. 설치 `CmdSession.Install` → `FindLinker`/`InstallLinker` → `LinkerInstaller`(`LinkerPlace`로 질문에 폴더를 적음).
- **inspect start|stop|result**: `CmdSession.Inspect` → `MainWindow.Inspect.cs`의 `GameSyncInspect`(`InspectRefusal`로 링크·연결·auto·레벨·같은 곡·NoFail 확인) → `GameSyncFollow(true)` → `LinkInspection`. 프레임마다 움직이는 시계에서만 `Plan`, 전용 스레드의 `Step`·`NextDueMs`가 노트 키를 보냅니다. Countdown의 첫 움직이는 프레임에 `WarmUp`(8개 레인 키·Space·Enter, 첫 노트 40ms 전 마감), 카운트다운 종료·정지는 `CancelWarmUp`. 처음 게임이 앞에 올 때까지 대기하며 이후 창 전환·계획 0.5초 공백 때 멈춥니다. 가운데 띠는 `ShowInspectBanner`, Esc는 `HandleObservedKey` 맨 앞. `-flow`는 `FlowLines`로 키·판정을 콘솔에 출력하고 `RunJob`·진행률·종료 콜백으로 프롬프트와 입력을 잠급니다. 진행 중 콘솔 세션은 `MainWindow._consoleSession`으로 다시 열어도 유지. 결과 `_inspected` → `ICmdHost.InspectLines(count)` → `inspect [-runs:n|*] result`. 키 보내기는 `MainWindow.KeySender`(시험용)·`KeyboardInjector.Send`, 링커 값은 `nf`·`hm`.
- **곡 낱말 link**: `CmdCatalog.SongGroups`·`KeywordText["link"]`, 명령 이름과 겹쳐도 되는 낱말 `SharedNames`(`NameClash`) → `CmdSession.GameSong()`(`SongFor`·`SongsFor`·`Load`의 `"link"`, 옵션은 `.`처럼) → `ICmdHost.GameSongId(out problem)` → `MainWindow.Console.cs`(링크·연결·레벨 확인 뒤 `SongFor(path, AllSongs, OriginalPathOf)`).
- **메시지 나이·타일 변경**: `MainViewModel.Message` setter가 시각을 새로 찍습니다(그래서 모든 새 메시지가 `(방금 전)`). `Reload(entry, saved)`가 슬롯 변경·저장 뒤 `TilesChanged`(`원본 → 수정본 n`·`TilesSaved`), `ApplySettings`·`RollBack`이 규칙이 바뀌면 `TilesChanged`+`TilesBySettings`. 편집기 저장 뒤 `MainWindow`가 `Reload(fresh, saved: true)`.
- **log**: `MainWindow.Log.cs`의 `LogFrame`(프레임마다 `FollowGameSync` 맨 끝에서, `Seen` 레코드로 지난 프레임과 비교해 바뀐 것만 `_events`(`Commands/EventLog`, 최근 5000줄)에, 플레이·follow 중엔 1초마다 스냅샷·`NotesOnScreen`, 연결 중엔 10초마다 게임 프로세스 `WindowsForegroundObserver.FindProcesses`) · 연결된 게임 pid는 `NamedPipeGameSyncServer.ClientProcessId`(`GetNamedPipeClientProcessId`) → `ICmdHost.LogLines`/`ClearLog` → `CmdSession.ShowLog`·`LogReport`(`report -log`).
- **link stop·auto**: `ICmdHost.GameSync(stop)`가 auto였으면 `GameSyncAuto(false)`. 따라가기가 스스로 끝나면(`_follower.OffsetMs`가 null) `_autoRefusedAt`을 지금으로 두어 auto가 `AutoRetryMs` 뒤 다시 시도. `load link`의 id는 `GameSongId`가 `_model.SongIdMap`으로 콘솔 id로 바꿔 넘기고, 레벨만 와도 됨.
- **update의 오래된 빌드**: `ICmdHost.StaleBuild()` → `MainWindow.Scan.cs`의 `BuildAge()`(`scannow app`과 공유) → `CmdSession.Update`의 `Behind == 0` 갈래가 `ReinstallAndRestart(["app"(, "linker")])`.
- **콘솔 언어**: `ConsoleText.Language`를 `CmdSession.Speak()`(생성·`Submit`마다, `ICmdHost.Language` = 설정 언어)와 `MainWindow.SpeakConsole()`(시작·`SettingsCommitted`)가 맞춥니다. `CmdSession.Language`(앱 쪽 UiText에 넘기는 언어)도 같은 값. 표 정렬은 `CmdSession.Wide`(한글 2칸), 날짜 요일은 `DateCulture` 키. `version`은 `Version(copy, check)`(`-check`만 `CheckUpdate`).
- **update**: `MainWindow.Console.cs`의 `UpdateScript(...)`(언제나 `update.ps1`)·`RunScriptAsync`(`step <0..1>` → 막대). `CmdSession.Update` → `CheckUpdate`(`-Check -Progress`, `state …` → `UpdateCheck.Read`) → `Confirm` → `ApplyUpdate`(`-WaitForProcessId -Restart [-Force] [-Version] [-LinkerGame]`, 출력 안 읽고 앱 종료). `install -reinstall linker`는 앱 안에서 `LinkerInstaller`, `source`·`app`은 `ReinstallAndRestart`, `scannow source`는 `-Scan`. 저장소 루트는 `WorkspaceMapCache.FindRepositoryRoot`. **앱이 다른 프로그램을 띄우는 유일한 곳**입니다.
- **프레임**: `MainWindow.InitializeInput`이 `MultimediaScheduling.Register()`(Closed에서 해제), GC 지연 모드는 `App.OnStartup`. `RenderFrame` → `FrameStats.Add` → 콘솔 `fps`(`FrameStats.Lines`). 같은 `RenderingTime`이 두 번 오면 한 프레임.
- **캐시 저장 범위**: 식별자·경로·곡 이름·원문 hash·파서 버전·상태·경고·hit time·타일 번호·hit 종류·체크포인트·미니게임 구간·곡 정보·강제 로드 형식. `ParserVersion`이 다르면 재동기화를 안내합니다.

## 4. 노트 규칙 요점

**모든 초기값의 원본은 `src/Visualizer.Core/Settings/DefaultSettings.jsonc`입니다**(코드 기본값을 직접 바꾸지 않음). 세부는 `docs/note-rules.md`.

- 모드 1·2·4·6·8, 경계 기본 `<50` 8K, `<70` 6K, `<120` 4K, `<500` 2K. 최대·최소 타일 수로 상·하한. 느린 쪽과 경계값이 같으면 그 모드를 건너뜀(`DynamicTimingSelector.Select`). 표시 레인 `DisplayLaneCount`(0 = 최대 타일 수)는 배치에 영향 없음(`GetShownTiles`·`GetDisplayTiles(쓰는 타일)`).
- 패턴은 타일 집합(오름차순 저장, `SortTiles`), 도는 순서는 `EnableTilePreference`가 꺼져 있으면 큰 번호부터. 1키 패턴 = `InitialTile`.
- 동타 레인 우선순위(`SimultaneousNotePlanner.Assign`의 level): ① 중앙에서 두 칸씩 묶은 쌍 ② 한쪽 반 안의 인접 쌍 ③ 중앙을 가로지르는 쌍(4레인은 `NoChordCenterPair`를 끌 때만) ④ 여분 레인. 선호 쌍 `GetPreferredChordLanes()`는 레벨 1부터. `ChordSectionLanes`는 동타 바로 뒤 단타가 그 레인을 피함.
- **한 손 흐름**(사용자 규칙): 커서는 패턴이 바뀌는 자리에서 직전 타일로 시작하지 않고(`TilePatternCursor.FreshStart`, `3 4 3`이 되면 두 번째부터), 트릴의 두 레인을 처음부터 정하며(`OpenTrill`: 후보 이웃 쌍·다음 칸·한 레인 뒤, 정렬은 흐름 위반 `Breaks`(`_closeToLast`일 때만) → 가운데 쌍(`Adjacent`가 허용, `TrillPair`로 뒤로) → 같은 쌍 반복, `_trillSecond`가 다음 호출에서 닫음), 가까운 트릴 앞 단타를 굴림의 머리로 옮기고(`RollHead`, `NoteGenerator`의 `leadsTrill`·`closeToLast`, `RollLeadWindows`), 트릴 뒤 단타를 굴러간 쪽 옆에 놓고 거기서 잇습니다(`BesideTrill` → `Place`, 끝이면 패턴대로). 트릴 묶음은 `SimultaneousNotePlanner.TrillStarts`, 쌍 판정 `IsTrillPair`, 방향 `Direction`. 동타 구간은 `Place`가 블록마다 `Flow`(커서 타일·`RoleOf`·방향·`Trailing`)를 넘겨 앞 사건을 피하는 방식(styles의 `Fresh`)을 먼저 시도하고, 동타 없는 구간은 겹치면 `Shift`, 마지막에 `SplitTrills`(방향·같은 쌍 회피·`ahead`, 가운데 쌍은 맨 뒤). 롱노트 레인은 `HeldUntil`(끝 뒤 첫 노트까지, `<=` 비교) — `NoteGenerator`의 `busyUntil`, `Place`의 `Add`/`Track`, `Shift`, `SplitTrills`의 `Free`가 같은 값을 씁니다. 빠른 반복: `NoteGenerator.QuickReturnMs`(빠른 쪽 경계 ×2)를 `FreshStart(tooSoon)`·`RestartCycle(tooSoon)`·`RollHead`의 `Clear(head, center)`·`OpenTrill`의 `Recent`(`_recent`, `NoteGenerator.played`)·planner `Rested`(`Flow.LastPlayed`)·`Shift`의 `Returns`가 함께 씀. 2키 즉시 올림은 `NoteGenerator`의 `crowded`(앞뒤 간격 평균), 앞뒤 간격 옵션은 `BothIntervalTiles`. 트릴 앞 단타 순서 `RollHead`(가운데 아닌 굴림 먼저, 끝에서 사이클이 도는 경우 유지)·planner `Heads`·`Opens`(가운데는 흐름이 끊길 때만), 끝에서 반대로 굴림은 `OpenTrill`의 `Back`·planner `RollsBack`·`SplitTrills`의 `rollsBack`. 향상된 동타 규칙은 `SimultaneousNotePlanner.LockChords`(`Lay`·`RollBeforeChord`, `UseEnhancedChordRules`).
- 화면은 최대 키 패턴 타일을 오름차순 레인으로, 여분 레인은 노트가 있을 때만(`GetDisplayTiles`). 동타는 `StartsChord`로만 그림.
- 마법진(같은 간격 ±5ms 연속)은 `MagicCircleFinder`(`Find(times, settings, chords)`: `MagicCircleChords`가 꺼지면 동타·단타가 같은 구간끼리만), 고정 타일은 `MagicCircleTiles`(clamp, 설정 값은 유지).
- 실험적 기능은 `ExperimentalFeatures`가 켜져야 적용: `UseChordAvoidBefore`(`Place`가 앞 단타를 옮김), `UseRestartOnRun`(`TilePatternCursor.RestartCycle`). `NextIntervalTiles`는 고급 설정. `UseChordSectionLanes`·`UseChordCenterPair`·`PreferredChordLane` 같은 계산 값은 예전 설정 파일 읽기용.
- 고급 설정 밖의 토글은 기본 켜짐이 되도록 이름을 정합니다(사용자 선호).

## 5. 문서 지도

| 문서 | 내용 |
| --- | --- |
| `docs/note-rules.md` | 설정 표, 동적 판정, 사이클, 동타·트릴, 레인 키 효과, 판정선, Reset/Start |
| `docs/adofai-parser.md` | 파서 지원 범위와 게임 파일 근거 |
| `docs/checkpoints.md` | 체크포인트 근거·자동 진행·연습 구간 시작 |
| `docs/environment-compatibility.md` | 버전·시작 대기·배속·일시정지·GPU |
| `docs/windows-input.md` | Raw Input 관찰, 레인 키, 전경 확인 |
| `docs/timing-comparison.md` | 독립 기준 계산 대조, 실제 플레이 확인 절차 |
| `docs/console.md` | 콘솔 명령·옵션·키워드·설정 이름의 원본 |
| `docs/architecture.md` | 계층·확장 지점·저장소 설계 |
| `docs/game-sync-plan.md` | 게임 동기화: 읽는 값과 근거, 파이프 형식, 시계 정렬 |
| `docs/long-notes-plan.md` · `map-storage-plan.md` · `song-list-plan.md` · `report-editor-plan.md` | 구현된 기능의 설계 요점 |
| `docs/future-plans.md` | 남은 계획 |
| `docs/demo-guide.md` | 시연 절차 |
| `docs/debug-logs/` | Unsupported·타이밍 어긋남 진단 절차와 해결 기록(날짜순 추가) |
| `docs/handoff-history.md` | 승인 원문과 1.0 이후 경위 (**평소에는 읽지 않음**) |

## 6. 코드 작성 관례

- 시간은 ms `double`이며 `double.IsFinite`·음수·비감소 검사를 거칩니다. 설정은 불변 `record`로 `with` → `EnsureValid()`. 공개 메서드는 잘못된 입력에 `ArgumentException` 계열.
- **주석**: 영어로 짧게, 코드만으로는 알 수 없는 **이유**만. 코드를 다시 말하는 주석·버전·날짜·요청 이력은 적지 않습니다(이력은 handoff-history). 사용자가 정한 규칙은 `(user rule)`, 게임 파일 근거(클래스·메서드)와 근거 맵 이름은 남깁니다.
- **토글 상태 문구**: `AddField`가 `CheckBox`를 받으면 `WithState`로 붙이고, 모든 토글은 `StateWords`에 쌍이 있어야 합니다(`AdvancedFeatureTests`). `InputFor`는 그 묶음에서 토글을 꺼냅니다.
- **설정판은 새 스냅샷을 만듭니다**: `BuildSettings()`에 없는 속성은 조용히 기본값으로 돌아가므로 화면에 행이 없는 설정도 실어 날립니다(`SettingsStoreTests`가 잡음). 콤보박스 항목은 언어 적용 때 채워지므로 `UpdateLanguage`에서 `_current` 값으로 되돌릴 수 있게 둡니다.
- **새 콘솔 명령·옵션**: ① 옵션(부사)인지 키워드(목적어·동사)인지 `docs/console.md` 2절로 가림 ② 옵션은 낱말 이름으로 `CmdCatalog.Options`에, 짧은 별칭은 `Short`, 두 낱말은 `이름:값`(`Values`·`Default` 필수) ③ `Options`·`Help`·`Summary`·키워드 뜻에는 긴 이름만 ④ 키워드는 `Keywords`와 `KeywordText`(명령마다 다르면 `"명령 키워드"`), 기본 키워드는 `*` ⑤ 키워드가 필수인 명령은 같은 이름의 옵션을 두지 않고, 키워드 없이 오면 받는 낱말을 알려 주고 멈춤 ⑥ `docs/console.md` 표 ⑦ `CmdSession`의 `switch`(수정 창을 건드리면 `NotWhileEditing`도). 어기면 `CmdConsoleTests`의 "Options and keywords keep to their rules"가 막습니다.
- **새 설정**: ① `VisualizerSettings` 속성(`DefaultSettings.Text/Number/Integer/Flag(nameof(...))`) ② `DefaultSettings.jsonc` ③ `Validate`/`ValidateFields` ④ 배치에 영향을 주면 `HasSameNoteRulesAs` ⑤ `SettingsPanel` 섹션·`BuildSettings`·`LoadForm` ⑥ **모든 언어 파일**에 라벨·`Hint.<설정>`·`?` 설명, 토글이면 `StateWords` ⑦ 잠기는 입력은 잠긴 동안에도 값 저장·복원(콘솔 `SettingPaths.LockedBy`) ⑧ `SettingPaths`와 `docs/console.md`. 이름을 바꾸면 `SettingsJson`이 예전 키를 읽게, 없애면 `Retired`에 넣습니다.
- 사용자에게 보이는 문자열은 언어 파일 키로만(Core 검증·JSON·배치 실패 메시지 포함). 콘솔 문구는 같은 파일의 `console` 묶음에서 `ConsoleText.T(키, 인자…)`(`using static`)로 읽습니다: 명령 설명은 `Summary.<명령>`·`Help.<명령>`, 옵션 `Option.<-이름>`(명령별은 `Option.<명령> <-이름>`), 키워드 `Keyword.<낱말>`(명령별 `Keyword.<명령> <낱말>`), 묶음 `Group.<묶음>`, 설정 섹션 `Section.<섹션>`, 따라가기 상태 `Follow.<상태>`. 명령·옵션·키워드 이름, 설정 경로·값, 곡 제목·경로는 번역하지 않습니다. 새 콘솔 문구는 두 언어 파일에 같은 키와 같은 `{0}` 자리로 넣습니다(`LocalizationThemeTests`가 짝·소스의 키 존재를 검사). 예외: 파서 경고와 `ParsedTimeline` 예외 문구는 한국어 고정.
- 한국어 UI는 "켜면/끄면" 대신 "활성화하면/비활성화하면", `Hint.*`는 "~니다." 한 문장.
- 색은 XAML·스타일에서 `{DynamicResource …Brush}`로만 참조하고, 새 색은 `ThemeCatalog.BrushKeys`와 모든 테마 파일에 추가합니다. 노트 영역 색은 테마가 아니라 설정에서 옵니다. 버튼·입력은 `Themes/Theme.xaml`의 키(`ToolButton`·`PrimaryButton`·`DangerButton`·`IconButton`·`PillToggle`·`StatePill`·`CompactToggle`), 재생 표면은 `PlaybackButton`/`PlaybackToggleButton`.
- ViewModel 명령은 필요한 속성만 알립니다(`NotifyAll`은 불러오기·동기화·설정 적용 같은 드문 작업에만). 바인딩이 값을 넘긴 setter는 같은 속성을 다시 알리지 않습니다.
- `catch`는 예상 예외 타입만. `NotePlacementException`은 ViewModel이 잡아 표시하고, 창 프로시저 안 키 처리는 예상 예외를 모두 잡습니다.
- `DllImport`는 Infrastructure에만 두고 `[SupportedOSPlatform("windows")]`.
- 파서는 미지원 조합을 `Unsupported`, 잘못된 JSON·필드를 `Error`로 반환하고, 캐시 내용이 달라지면 `CurrentVersion`을 올립니다.
- 렌더 경로는 브러시·펜을 Freeze하고 보이는 노트 구간만 이진 탐색으로 순회합니다. 렌더링을 바꾸면 이전 렌더러와 번갈아 프레임 간격을 비교합니다(이 PC는 같은 코드도 8~12ms로 흔들림).

## 7. 테스트 하네스

`tests/Visualizer.Tests/Program.cs`의 `Main`이 `(string Name, Action Run)` 목록을 실행하고 `N/M test groups passed`를 출력합니다. 인자는 artifact 절대 경로, 선택 `--read-only-map-root PATH`·`--compare-game-rules`. 새 테스트는 해당 파일의 `Create(...)`에, 새 파일은 `Program.cs`의 `.Concat(...)`에. 헬퍼 `Program.Assert/Equal/Sequence/Throws/Capture`.

파일별 담당: `Program.cs`(기본·노트 규칙) · `ExtendedTests` · `PlumExampleTests` · `SimultaneousNoteTests`(동타·트릴 흐름) · `SixKeyTests` · `EnvironmentCompatibilityTests` · `CheckpointControlTests` · `CheckpointLayoutTests` · `RhythmVisualTests`(레인 키·판정·캡처) · `PracticeSectionTests` · `AdvancedFeatureTests`(설정판·메인 화면·검색창) · `ExtraLaneTests` · `LocalizationThemeTests`(언어·테마·곡 정보·릴리스 노트) · `DesignAndTimingTests` · `LongNoteTests` · `SettingsStoreTests` · `SongListTests` · `NoteEditTests` · `CmdConsoleTests` · `GameSyncTests`(링크·파이프·링커·설치·제거 스크립트) · `MapCompatibilityTests`. `GameRuleTimingComparison`은 `-CompareGameRules`용 독립 계산기입니다.

함정:

- 설정 테스트에서 콤보박스를 순서로 찾지 않습니다. 최대·최소 타일 수는 `ItemsSource is int[]`, 입력은 `SettingsPanel.InputFor(설정이름)`.
- 콘솔은 앱 언어를 따르므로 영어 문구를 읽는 시험은 영어로: 시험 하네스가 그룹마다 `ConsoleText.Language`를 영어로 되돌리고, `CmdConsoleTests`의 가짜 `Host.Language`는 기본 영어(설정 언어와 따로), 창을 쓰는 시험은 `Program.English(new MainWindow())`(언어를 영어로 적용하고 이력은 지움).
- 설정판은 `new SettingsPanel(settings, new MemorySettingsLayoutStore())`, 섹션 상태 `SectionState("SectionTiming")`, 도움말은 "?" 배지의 `ToolTip`. 행은 카드 안이라 라벨은 논리 트리 전체 검색. 메인 창 안에서는 `SettingsButton`을 눌러 `SettingsHost.Child`.
- 순서가 중요한 테스트는 `EnableTilePreference = true`와 선호도를 함께 지정합니다. 기본값에 기대는 테스트는 값을 직접 지정합니다(경계값·색·투명도에서 실제로 깨졌음).
- 설정을 바꾼 뒤 캡처할 때는 `field.Tick()`.
- 저장소 테스트는 `AppStorage.At(<테스트 폴더>)`(`AppStorage.Open`은 실제 저장소). scannow 시험은 `MainWindow.StoreForTests`를 쓰고 `finally`에서 비웁니다.
- 픽셀 테스트는 첫 노트가 시작 노트 색이라는 것을 기억합니다(두 번째 이후 노트를 보거나 `StartNoteColor`를 노트 색으로).
- 창을 띄우는 테스트는 `Left = -20000, Top = -20000, ShowActivated = false`. 키 이벤트는 실제 순서(PreviewKeyDown → KeyDown → KeyUp)로, Ctrl 같은 수정 키는 누를 수 없으므로 그 키가 부르는 메서드를 직접 부릅니다.
- 결함을 고치는 테스트는 고치기 전 코드에서 실패하는 것을 확인합니다(되돌릴 때는 Python으로 고치고 되돌려 수정 시각 갱신).
