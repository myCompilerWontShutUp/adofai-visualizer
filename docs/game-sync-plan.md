# 게임 시작 동기화 (game sync)

게임이 곡을 시작하는 시각을 받아 노트를 게임과 같은 시각에 떨어뜨리는 기능의 설계·근거입니다. 안전 규칙은 `AGENTS.md` 2·6절.

## 1. 목표와 범위

- **시작 동기화**: 시작 키 없이, 게임이 곡을 시작하면 같은 시각에 노트를 내리고 **3·2·1 카운트다운 동안 미리 내려옵니다**.
- **타일 대조**: 게임이 계산한 타일 시각과 우리 hit 시각을 비교합니다. 자동 테스트·독립 계산기는 같은 IL 해석을 공유하므로, 이것이 **게임 자체와 맞춰 보는 유일한 수단**입니다.
- 후순위(사용자 결정): 곡 자동 불러오기, 게임 설정 맞추기, 체크포인트.
- 범위 밖: 게임에 입력을 보내기, 게임 값을 바꾸기, 게임 로직 패치.

## 2. 지키는 선

- 모드는 값을 **읽어 파이프로 내보냅니다**. 게임 필드에 쓰지 않고, 입력을 만들지 않으며(`OnUpdate` 폴링), Harmony 패치는 **판정 완화 하나**뿐입니다(사용자 승인 2026-11-17): `JudgmentEase`가 `scrMisc.GetMinimumTimes`의 모든 `ldc.r4`(시간 상수 0.091/0.065/0.04·0.03·0.02·모바일 값·바닥 0.025) 뒤에 배율을 곱하는 호출을 넣는 트랜스파일러라, 게임 타입을 참조하지 않고 최소 시간 결과 전체가 배율배가 됩니다(판정과 게임 판정 막대 모두). 배율은 앱이 저장소 집(`%LOCALAPPDATA%\AdofaiVisualizer\judgment-ease.txt`)에 쓰는 값을 1초마다 다른 스레드에서 읽고, `Visualizer.App` 프로세스가 없으면 1입니다. 패치하지 못하면 UMM 로그에 이유를 남기고 그 기능만 꺼집니다. 끄거나 내리면 자기 id로만 되돌립니다(`UnpatchAll(id)`). 읽기 줄의 `ez`가 지금 적용 중인 배율입니다.
- 게임 타입은 리플렉션으로 찾습니다(`Assembly-CSharp` 참조 없음). 이름으로 필드든 속성이든, static이든 아니든 읽습니다(`GameReader.Member`). 멤버가 아예 없을 때만 그 기능을 끄고(`Problem`) 게임은 그대로 돕니다.
- **게임이 스스로 던지는 예외**(장면 정리 중, 곡이 아직 없음)는 그 프레임 읽기만 버립니다(`Trouble`, 연속 1초가 넘으면 30프레임에 한 번만 시도). 파괴된 Unity 객체는 없는 것으로 봅니다. 보여 주기만 하는 값(보정·pitch·일시정지·타일·연습 배속·죽음 목록)이 실패해도 곡 위치는 보냅니다. 곡 위치가 숫자가 아니면 보내지 않습니다.
- `OnUpdate` 전체를 감싸 어떤 예외도 게임으로 던지지 않고, 같은 오류는 한 번만 기록합니다. 보내는 스레드는 어떤 예외에도 죽지 않고 파이프를 버린 뒤 다시 연결하며, 로그는 큐에 담아 게임 스레드가 UMM 로거로 넘깁니다(UMM 로거는 다른 스레드용이 아님).
- Unity 메인 스레드를 막지 않습니다(값만 복사, 보내기는 별도 스레드). 앱이 없으면 아무 일도 하지 않습니다. 키뷰어 모드가 없으면 그 타입 찾기는 창 포커스가 바뀔 때만 다시 합니다(매 프레임 어셈블리 전체를 뒤지지 않음).
- 앱은 모드 없이도 똑같이 동작하고, 받은 값은 검사(범위·단조 증가)하며 저장하지 않습니다.
- 유일한 쓰기 예외: JipperResourcePack 키뷰어 GameObject를 게임 창이 전경이 아닐 때 끄고 돌아오면 켭니다(`KeyViewerFocus`, 모드를 내리면 다시 켬).

## 3. 게임에서 읽는 값

ReflectionOnly 조사로 확정(덤프 `.local/sync-signals.txt`·`sync-details.txt`·`il-songpos.txt`).

| 값 | 위치 | 쓰임 |
| --- | --- | --- |
| 곡 위치 | `scrConductor.instance.songposition_minusi` (초) | 노트가 따라가는 시계 |
| 보정 | `calibration_i`·`calibration_v` (초) | 표시용 |
| 배속 | `song.pitch` | 실제 재생 속도(`GCNS.practiceSpeedMultiplier`는 연습 모드 설정값이라 보여 주기만) |
| 상태 | `scrController.instance.currentState` | `None, Start, Countdown, Checkpoint, PlayerControl, Fail, Fail2, Won` |
| 일시정지·타일 | `scrController.instance.paused`·`currentFloorID` | 일시정지 / 진단. 타일은 컨트롤러 하나 안에서 올라가기만 합니다(`scrController.Update`가 `Math.Max(currentFloorID, currFloor.seqID)`로만 씀, IL 확인) — 재시작 뒤에도 지난 판이 닿은 타일 |
| 판정 난이도·판정 이름 | `GCS.difficulty`(줄의 `df`) · `HitMargin` 열거 이름(연결 인사 줄의 `hmn`) | 판정선의 경계(난이도 최소 시간)와 판정 개수(`hm`)를 이름으로 읽기. 2026-11-04 게임 빌드가 `PerfectMinus 3`·`XPerfect 4`·`PerfectPlus 5`를 넣어 뒤 번호가 밀림(`FailMiss` 8 → 10 등) — 이름이 없는 이전 링커는 길이(12 이전 · 16 이후)로 구분 |
| 재시작 타일 | `GCS.checkpointNum` (static, 읽기만) | 다음 판이 서는 타일: `ffxCheckpoint.StartEffect`가 부활 타일로 올리고, `SetPracticeMode`·`scnEditor.Play`가 시작 타일, `RestartProgress`가 0으로, `Start_Rewind`가 그 타일에서 시작. 줄의 `cp`(읽지 못하면 뺌) |
| 곡 파일 | `scnGame.instance.levelPath` | 같은 곡인지 판단 |
| 타일 시각 | `scrLevelMaker.instance.listFloors[i].entryTime` (초) | 레벨을 불러올 때 한 번 |
| 죽음 | `scrController.deathStates` | 게임 오버면 노트를 세움 |

모드는 곡 위치를 **`LateUpdate`에서** 읽습니다. `scrConductor.Update`가 프레임마다 `(float)(dspTime − dspTimeSong − calibration_i) × pitch − addoffset`로 정하는 필드이고(`dspTime`은 오디오 시각이 바뀌지 않은 프레임 동안 `unscaledTime` 차이로 메움, `.local/il-conductor-update.txt`), `Update`에서 읽으면 실행 순서에 따라 한 프레임 전 값이라 연출이 무거워 프레임이 길어지는 곳일수록 늦습니다.

**입력 보정 쪽(`minusi`)을 따릅니다.** 게임은 `songposition_minusv = songposition_minusi + calibration_i − calibration_v`로 화면·입력 보정을 나누는데, 누름을 판정하는 기준이 `minusi`이고 떨어지는 노트는 "언제 누르는가"를 알려 주는 것이기 때문입니다(`minusv`를 쓰면 게임 오프셋만큼 노트가 빨리 나옴).

## 4. 통로

- 명명 파이프 `\\.\pipe\adofai-visualizer-sync`, 단방향(모드 → 앱), 줄 단위 UTF-8 JSON. 앱이 서버이며 `link start`를 할 때만 엽니다. 이름이 하나라 이미 다른 앱이 쥐고 있으면 그렇다고 알리고 2초마다 다시 시도합니다(비주얼라이저 두 개가 떠서 게임이 먼저 켠 창에 붙고 나중 창의 `link auto`가 아무것도 하지 않던 사용자 제보 뒤로 앱은 하나만 실행됩니다 — `SingleInstance`)(테스트는 `MainWindow.GameSyncPipeName`).
- 모드는 연결될 때마다 자기 버전(`{"v":2,"mod":"x.y.z"}`)과 기억해 둔 타일 시각을 먼저 보냅니다(게임을 먼저 켜도 됨). 메뉴로 나가면 보낸 기록을 잊어 같은 곡을 다시 들어가도 보냅니다. 타일이 만들어지기 전(목록이 비었거나 시각이 모두 같음)이면 프레임마다 다시 시도하고 600프레임 뒤 그만둡니다. 보낸 지 60프레임 뒤 한 번 더 읽어 달라졌으면 다시 보냅니다.
- 앱은 연결마다 `GameSyncLink.BeginConnection`으로 이전 연결의 모드 시계·마지막 읽기·타일 시각·모드 버전을 버립니다. 게임을 다시 켜면 모드 시계가 0부터 다시 시작하므로, 이것이 없으면 모든 읽기를 "시계가 거꾸로 감"으로 거절했습니다. 한 줄이 이상해도(빈 버전 등) 그 줄만 거절하고 연결은 유지하며, 파이프 오류·권한 오류는 2초 뒤 다시 엽니다(`Listening`은 루프가 실제로 도는지를 봄). 연습 배속은 보여 주기만 하므로 범위를 벗어나도 읽기를 거절하지 않습니다.
- `Info.json`과 모드 버전은 빌드가 `Directory.Build.props`의 `<Version>`에서 만듭니다. 콘솔 `link`의 `Mod` 줄이 앱 버전과 대조합니다.
- 직렬화는 `SyncWriter.Line`이 만들고 `GameSyncTests`의 파이프 시험이 같은 줄을 넣습니다. 이후 링커가 더한 필드(`nf`·`hm`·`cp`)는 이전 앱이 무시하고, 이전 링커가 빠뜨리면 앱은 모르는 것으로 둡니다(프로토콜 번호는 2 그대로).

## 5. 타일 대조 (`link tiles`)

게임의 `scrFloor.entryTime`과 불러온 곡의 hit 시각(`ParsedTimeline.HitFloors`로 타일 번호를 대입, 중간 회전은 자연히 빠짐)을 뺍니다. 둘 다 같은 파일의 일정표라 같게 읽었으면 차이가 **모든 타일에서 1ms 안에 같습니다**. 한 타일만 튀면 그 타일이 원인입니다(`docs/debug-logs/timing-mismatch.md`).

- `currentFloorID`가 바뀐 순간의 곡 위치는 쓰지 않습니다. 그 값은 플레이어가 타일을 **밟을 때** 올라가 타건을 잽니다.
- pitch≠100 곡은 모드가 보내는 `entryTime`이 pitch로 나누기 전 값이라 차이가 일정하지 않아 거절합니다.

## 6. 따라가기 (`link follow`·`link auto`)

- `link tiles`의 오프셋을 기준으로 **앱의 재생 지점만** 게임 곡 위치에 맞춥니다(노트·hit 시각·판정은 그대로). 차이가 60ms를 넘으면 즉시 옮기고, 작으면 프레임마다 15%씩 당깁니다. 카운트다운의 음수 곡 위치 덕분에 3·2·1 동안 노트가 미리 내려옵니다.
- **노트가 서서 기다리는 경우**(앱이 혼자 흘러가지 않음, `GameSyncFollower.Stand`): 게임 일시정지·곡 위치가 150ms 넘게 그대로(`StillMs`)면 게임이 말한 위치에 그대로 세우고, 읽기가 0.5초 넘게 끊김(`Silent`)·다른 레벨(`OtherLevel`)·게임 오버(`GameOver`)·게임 실제 속도≠100%(`OtherRate`, 노트는 100%로만 떨어짐)면 그 순간 자리에 세웁니다. 다시 맞는 읽기가 오면 이어서 따라갑니다. 아직 시작 전이면 곡 위치가 실제로 움직이는 것을 본 뒤에만 시작합니다(시작 화면에 머문 게임으로 시작하지 않음).
- 게임 오버는 일시정지·끊김보다 먼저 봅니다(1.0.1은 일시정지를 먼저 봐서, 실패 화면이 일시정지로 잡히거나 재시작하며 레벨을 다시 불러오는 동안 읽기가 끊기면 노트가 흘렀음). `deathStates`는 정적 생성자가 `{Fail, Fail2}`로 채우는 static 배열입니다(`.local/inspect-cctor.ps1`). 레벨이 첫 키를 기다리는 `Start` 상태(재시작해도 여기로 옴)에서는 게임 곡 위치가 실시간으로 계속 흐르므로(PLUM MEGAMIX 2 시작 화면에서 3초에 약 2992ms, 2026-10-12 실측) 그동안도 노트를 세웁니다 — 죽은 뒤 재시작까지 노트가 흐르던 실제 원인.
- **되감기의 숨김**(사용자 규칙: 보이면 안 되는 노트는 그 판에서 치지 않는 노트뿐): 게임이 60ms 넘게 뒤로 가면 재시작 타일까지의 노트를 숨기고(`HideNotesBeforeMs`, `GameSyncFollower.RestartCut`), 나머지는 카운트다운 동안 내려옵니다. 재시작 타일 위에 선 채 카운트다운을 하므로 그 타일 자신의 hit도 치지 않습니다(게임의 체크포인트·연습 시작이 그 뒤 첫 hit부터) — 자르는 곳은 그 타일과 다음(더 늦은) 타일의 가운데(`CutAfter`, 1ms 안팎의 시각 차이에 흔들리지 않게). 아직 시작 전인 앱이 게임과 함께 시작할 때도(연습 모드·에디터의 중간 시작) 같은 타일까지 숨깁니다.
  - 재시작 타일은 링커의 `cp`(`GCS.checkpointNum`)입니다. 예전에는 게임 타일(`currentFloorID`)을 믿었는데, 그 값은 레벨 안에서 올라가기만 해서 죽은 뒤 카운트다운 내내 죽은 타일을 가리켰고, 그 앞 노트가 모두 숨어 카운트다운이 끝난 뒤에야 나왔습니다(사용자 제보: 처음부터·체크포인트·연습 모드 모두, 보통 한 번 죽은 뒤). 같은 이유로 게임 에러 뒤 연습 재시작에서 타일 745가 곡 위치(타일 700 부근)보다 2.6초 앞섰던 제보도 있었습니다(Wont Let You Go).
  - `cp`가 없는 이전 링커: 곡 위치에서 -60ms~6초 앞의 체크포인트 부활 타일 중 지난 판이 닿은 가장 늦은 것, 없으면 곡 위치 뒤만 숨깁니다(죽은 타일은 믿지 않음). 뒤 읽기의 타일이 더 낮으면(레벨을 새로 불러온 컨트롤러) 숨김을 그 타일까지 줄이고, 플레이 중(`PlayerControl`)에 타일이 곡 위치보다 `AheadTileMs`(300ms) 넘게 앞이면 숨김을 곡 위치까지 내립니다.
  - `link stop`은 곡을 불러온 직후 상태로 되돌립니다. 콘솔 `log`의 게임 상태 줄에 재시작 타일이 함께 남습니다.
- 같은 레벨의 타일 시각이 다시 오면(재연결마다 옴) 같으면 그대로, 다르면 다시 대조해 어긋나면 따라가기를 끝냅니다(`LevelChanged`). 프레임 루프 안의 따라가기는 예상 예외를 잡아 따라가기만 끝내고 이유를 `link`의 `Problem` 줄에 남깁니다.
- **거절**: 타일 불일치·다른 곡·곡 중간부터 준비됨(체크포인트·구간)·앱 배속≠100%·곡 없음·꺼짐. 이유를 구분해 말합니다.
- **따라가는 동안**(`PlaybackSession.ClockFollowsGame`) 앱이 곡 위치를 스스로 옮기는 동작은 모두 멈춥니다(사용자 규칙): 일시정지 정렬, 레인 키 재개의 이동, 체크포인트 자동 진행, 자동 표시 보정(멈춘 값은 끄면 그대로 돌아옴). 곡·구간·체크포인트·배속이 바뀌면 따라가기를 끝냅니다(`Dropped`, `link`가 이유를 적음). 켜고 끄는 지점은 `MainViewModel.FollowingGame` 하나이고, 상태가 바뀌면 `NotifyPlaybackChanged()`로 화면에 알립니다.
- Esc는 따라가기보다 먼저입니다(앱에서 멈추면 기다리고, 이어 재생하면 다시 붙음). 게임 오버(`deathStates`)면 노트가 그 자리에 서고, 체크포인트로 되감으면 그 앞 노트를 그리지 않으며(`HideNotesBeforeMs`) 눌림·놓침 표시도 되감습니다(`NotePressJudge.ForgetAfter`).
- **`link auto`**: 게임이 다른 곡으로 옮기면(게임 레벨 경로가 불러온 곡과 다르면) 그 곡을 불러옵니다(사용자 규칙). 고르는 곡은 원곡, 목록에 원곡이 없으면 첫 수정본(`MainWindow.SongFor`), 불러오기는 콘솔 `load`와 같은 길(`LoadChosen`, 배속 규칙 포함)입니다. 게임 곡마다 한 번(`_autoLoadedPath`, 불러온 곡이 게임 곡과 같아도 기록)이라 손으로 고른 곡은 게임이 다시 옮길 때까지 그대로이고, 수정 창·설정판이 열려 있으면 닫힐 때까지 기다리며, 목록에 없으면 동기화하라고 알립니다. 불러온 곡이 게임 곡과 같으면(게임 경로가 우리 상대 경로로 끝나는지; 수정본은 `maps/edited/<원곡>/<슬롯>/`에 있어 원곡 폴더·파일 이름으로 — `MainWindow.GamePlays`) `link follow`와 **같은 함수**로 시도합니다. 경로로 못 찾으면(직접 넣은 맵을 게임이 다른 폴더·파일 이름으로 열었을 때, 사용자 제보) 게임이 보낸 타일 시각으로 찾습니다(`GameTileCheck.LinesUp`: 모든 hit의 타일이 한 오프셋으로 1ms 안에 맞고 마지막 hit가 레벨 끝 — 긴 맵의 앞부분과만 맞는 짧은 맵은 아님. 원곡 → 게임 파일 이름과 같은 것 → 수정본 순, 레벨 객체마다 한 번 비교). `load link`·`inspect`·`link tiles` 경고도 같은 판단을 씁니다. pitch≠100 곡은 타일 시각이 맞지 않아 경로로만 찾습니다. 열쇠는 레벨 객체(다시 보낸 레벨은 새 객체)·곡 id·**준비 횟수**(`PlaybackSession.Prepared`, 실패한 준비 포함 — 배속을 되돌린 뒤에도 다시 시도하도록)이고, 거절되면 2초 뒤 다시 시도합니다(게임이 곡을 옮기는 도중 덜 만들어진 타일로 거절된 뒤 멈추던 문제). 이유는 `link`의 `Auto` 줄에 남깁니다. UI·설정이 아니라 콘솔 `link auto`·`link manual`이며 저장하지 않습니다. 켜져 있는 동안 위치를 옮기는 키는 무시합니다(`InputPolicy.MovesPlayhead`). 그래서 거절이 `link`에만 남으면 앱이 멈춘 것처럼 보이므로(사용자 제보 — 이전 파서 캐시의 Miku) 메인 화면에 시도마다 한 번 알립니다: `GameAutoFollowing` · `GameAutoRefused` · `GameAutoStaleCache`(캐시의 `ParserVersion`이 현재 파서와 다름 — `MainViewModel.IsStale`). 같은 열쇠의 재시도는 메시지를 다시 쓰지 않고, 콘솔 거절 첫 줄에도 이전 캐시면 `run sync`를 적습니다.

## 7. 링커 설치·제거

- `install linker`: 대상은 `<게임>\Mods\AdofaiVisualizerSync\` 하나, 쓰는 파일은 `AdofaiVisualizerSync.dll`·`Info.json` 둘뿐입니다. Unity Mod Manager가 없으면 아무것도 쓰지 않고, 같은 버전이면 그대로 두며, 다른 버전이면 묻습니다. 길에 junction·symlink가 있으면 거절합니다. 게임 폴더는 설정의 맵 폴더가 가리키는 스팀 라이브러리(`steamapps\common\A Dance of Fire and Ice`), 없으면 기본 설치 경로입니다.
- 모드 파일은 앱 옆 `plugins\linker\` → `linker\` → 앱 폴더 → 저장소 `plugins/linker` → `tools/GameSyncMod/bin/{Release,Debug}` 순으로 찾습니다. 빌드된 모드는 저장소 `plugins/linker/`에 커밋되어 다른 PC는 빌드하지 않습니다(그 PC에는 .NET Framework 4.8 참조 어셈블리가 없어 NU1100으로 실패했음). `update`·`install -reinstall linker`도 이 파일을 복사합니다.
- 제거(`uninstall.ps1`)는 그 폴더를 통째로 지웁니다(UMM이 옆에 만드는 `AdofaiVisualizerSync.dll.<숫자>.cache` 포함, `Info.json` Id가 다르면 남김). UMM의 `Params.xml`은 건드리지 않습니다 — `UnityModManager+Param::Save`가 `ModParams.Clear()` 뒤 불러온 모드로 다시 채우고 게임을 끌 때 이를 부르므로(IL 확인) 그 줄은 저절로 빠집니다. 게임이 실행 중이면 멈춥니다.

## 8. 실측 (사용자)

| 잰 것 | 값 |
| --- | --- |
| 읽기 | 3182개, 거절 0 |
| 도착 간격 | 중앙값 16.7ms (게임 60fps) |
| 지연 흔들림 | p95 +0.1ms · 최대 +2.0ms |
| 카운트다운 | 곡 위치 -2301ms · -441ms에서 `Countdown` |
| 조작 시작 | 두 판 모두 약 1220ms에서 `PlayerControl` |
| 실패 뒤 | `Fail`·`Fail2` 동안에도 곡 위치가 계속 흐름(95초까지) |
| 타일 대조 | Cardboard box - The Limit Does Not Exist 1360타일 전부 오프셋 692.3ms, 퍼짐 0.0ms |
| 실제 플레이 | 215초, 읽기 10828개·거절 0, 되감기 27회, "매우 정확하며 esc 후 다시 실행해도 잘 돌아감" |

파이프는 문제가 아니고 한계는 게임 프레임(16.7ms)입니다.

## 9. 확인하지 못한 것

- 게임 배속이 100%가 아닐 때 곡 위치가 실제 시간보다 빨리 흐르는지(그래서 지금은 따라가지 않고 기다림).
- 1.0.2 링커가 실제 게임에서 일으키는 예외 기록(UMM 로그)과 일시정지·재시작·레벨 이동 때의 체감.
- 모드 사용이 리더보드·업적에 주는 영향, 다른 모드(AdofaiTweaks·JALib 등)와의 상호작용(폴링만 하므로 가능성은 낮음).
- 키뷰어를 끄는 동안 그 모드의 키 개수 세기가 멈추는지(보이는 것만 확인).

## 10. inspect start|stop|result (측정 전용)

- 목적: 비주얼라이저가 그린 노트대로 게임에 키를 눌러, 게임이 비주얼라이저 타이밍을 어떻게 판정하는지 봅니다(게임 타일을 읽고 누르는 것이 아님 — 사용자 규칙).
- 원칙(모두 지켜질 때만, 시작할 때와 매 프레임): 링크 켜짐·`link auto` 꺼짐, 게임과 같은 곡, 게임의 실패 방지 모드 켜짐(링커가 `scrController.noFail` → 없으면 `GCS.useNoFail`을 `nf`로 보냄; 모르면 거절), 게임 프로세스가 앞에 있음(키를 보낼 때마다 `ForegroundProcessId`로 확인).
- 판정: 링커가 `scrPlayerManager._instance.mistakesManager.marginTrackers[0].hitMarginsCount`(HitMargin 순서 — 2026-11-04 이전 빌드 기준: TooEarly 0, VeryEarly 1, EarlyPerfect 2, Perfect 3, LatePerfect 4, VeryLate 5, TooLate 6, Multipress 7, FailMiss 8, FailOverload 9, Auto 10, OverPress 11. 이후 빌드는 PerfectMinus·XPerfect·PerfectPlus가 끼어 `inspect`의 이 번호는 틀림, 확인 대기)를 `hm`으로 보냅니다(바뀔 때만 새 배열). 앱은 양의 증가를 누적하고, 게임 재시작으로 개수가 줄면 새 회차의 0을 기준으로 세어 첫 표본에 이미 있는 판정도 포함합니다. TooEarly·TooLate·FailMiss·FailOverload는 증가를 관측한 현재 타일과 마지막 입력을 기록합니다(실제로 놓친 타일을 확정하지 않음).
- 키: 설정의 레인 키(`ControlBindings.CreateLaneKeys`를 뒤집음)를 스캔 코드로(`KeyboardInjector`). 탭은 25ms, 롱노트는 끝까지 누름. 전용 스레드가 마지막 20ms를 스핀해 맞춥니다. 같은 레인을 빠르게 누르면 KeyboardChatterBlocker 같은 다른 모드가 막을 수 있습니다(기록에 판정으로 드러남).
- 게임 찾기: 시작할 때 게임 프로세스를 이름으로 찾습니다(`WindowsForegroundObserver.FindProcess`, 설정의 게임 실행 파일 이름). 예전에는 게임이 앞에 있는 프레임에서야 잡았기 때문에, 콘솔이 앞에 있는 채로 첫 노트가 오면 "the game window is not in front"로 키 하나 없이 끝났습니다(사용자 메모 "inspect test 1").
- 대기: 게임이 **처음 앞에 올 때까지** 실행은 기다리고, 그동안 판정선에 닿은 노트는 키 없이 넘기며 개수를 셉니다(`LinkInspection.Skipped`, 결과의 `waiting:` 줄). 한 번 앞에 온 뒤 게임이 앞에서 사라지면 바로 멈추고, 이유에 그때 앞에 있던 프로세스 이름·pid와 게임 pid를 적습니다.
- 사전 입력: 카운트다운의 첫 움직이는 프레임에 설정의 8개 레인 키·Space·Enter를 한 번씩 누릅니다(40ms 간격·25ms 탭). 첫 노트 100ms 전에 끝나도록 제한하며, 시간이 모자라면 들어갈 키만 예약합니다. 콘솔이 앞에 있어도 예약을 소비하지 않고, 게임 창이 앞으로 온 뒤 실제 전송 사이 40ms 간격을 유지합니다. 마지막 사전 입력은 25ms 탭까지 포함해 첫 노트와 125ms 넘게 떨어집니다(확인된 100ms 채터링 필터와의 충돌 방지). 카운트다운 종료·일시정지는 예약을 취소하고 사전 입력 키를 뗍니다. 기다리는 시계에서는 노트를 소비하지 않고, 판정선에 정확히 닿은 첫 노트도 누릅니다. 게임 버퍼 원인은 사용자 제보이며 실제 게임 개선은 확인 대기입니다.
- 표시: 진행 중에는 노트 영역 가운데에 안내 띠가 뜹니다(`InspectBanner`, 대기 중·누르는 중과 보낸 키 수, Esc로 멈춤 — 사용자 요청).
- 멈춤: Esc, `inspect stop`, `link stop`, `link auto`, 따라가기 끝, 노트 재준비, 다른 곡, 실패 방지 모드 꺼짐, 게임이 (한 번 앞에 온 뒤) 앞에서 사라짐, 앱 화면이 0.5초 넘게 계획을 안 줌(최소화 등), 곡 끝. 멈출 때 누른 키는 모두 뗍니다.
- 기록: 콘솔 `log`가 링크·링커(연결된 게임 pid, 실행 중인 게임 수)·레벨·게임 상태·follow·auto·노트 준비와 1초 스냅샷을 메모리에 남깁니다(`docs/console.md`). 얼불춤이 두 번 실행되면 파이프는 한 번에 하나만 받으므로 먼저 붙은 쪽을 따릅니다 — 기록의 pid로 어느 게임인지 봅니다.
- 재시작: 새 inspect는 앱에 남은 일시정지를 풀고 게임 시계에 맡깁니다. 이전 재생 위치의 지나간 노트만으로 새 측정을 끝내지 않습니다. 게임이 60ms 넘게 뒤로 되감기면 이전 키·예약을 해제하고 그 위치부터 노트를 다시 계획하며 사전 입력도 다시 준비합니다(작은 시계 보정은 노트를 반복하지 않음). 체크포인트가 숨긴 앞 노트는 보내지 않고, 사전 입력의 마감은 다음 표시 노트를 기준으로 정합니다.
- 실시간 출력: `inspect -flow start`(`-f` 별칭)는 키·노트·레인·곡 시각·원본 맵 타일·관측한 현재 게임 타일·앱 예약 대비 지연·같은 키 전송 간격과 문제 판정을 한 줄씩 출력합니다. 측정 종료까지 콘솔 프롬프트·입력을 잠그고, 마지막 판정과 종료 이유를 출력한 뒤 돌려줍니다. 콘솔을 다시 열어도 진행 중 세션을 유지합니다.
- 결과: `InspectRun.Lines()`는 시작 당시 노트 설정·실제 패턴 순서·설정 레인 키·배속·시작 대기·표시 보정, 보낸 키 수·앱 예약 대비 전송 지연·앱 프레임 공백, 관측 게임 표본 간격(중간 읽기 생략 가능), 같은 키의 최단 전송 간격(사전 입력 포함), 판정 변화·문제 판정의 관측 타일·마지막 입력 대상 원본 타일을 보여 줍니다. 설정 키 이름은 사용자가 고른 바인딩이며 사용자의 관찰 입력 이력은 수집하지 않습니다. 앱이 늦지 않았다는 결과만으로 게임이 키를 받았다고 판단하지 않습니다. `inspect result`는 마지막 실행, `inspect -runs:n result`는 최근 n개(기본 1), `inspect -runs:* result`는 전체입니다. 앱을 끌 때까지만 메모리에 둡니다. 기존 -inspect 옵션은 제거했습니다.
