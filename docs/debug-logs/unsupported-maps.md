# 디버그 기록 1 — Unsupported 곡

## 증상

- 곡 목록 드롭다운의 제목 오른쪽에 빨간 `Unsupported`(또는 `Error`)가 붙습니다.
- 불러오면 곡이 준비되지 않고 메인 화면 아래에 이유가 나옵니다. 콘솔은 `The map is Unsupported.`를 출력합니다.
- 파서는 확인하지 않은 규칙을 추측하지 않고 첫 번째로 걸린 이유 하나로 멈춥니다. 그래서 이유 하나를 고치면 **다음 이유가 드러날 수 있습니다**(Up In My Jam이 그랬습니다).

## 진단 순서

1. **캐시에서 이유를 읽습니다**(`README.md`의 스크립트). `Warnings[0]`이 이유입니다.
2. **`ParserVersion`을 `AdofaiTimelineParser.CurrentVersion`과 비교합니다.** 다르면 코드보다 먼저 빌드·동기화를 의심합니다. 흔한 원인은 두 가지입니다.
   - 사용자가 옛 Release를 실행 중입니다. 앱이 출력 폴더를 잠가 Release가 빌드되지 않았고, Debug만 새것입니다.
   - 새 빌드로 Map Sync를 하지 않았습니다.
3. **이유를 분류합니다.**

   | 이유 문구 | 할 일 |
   | --- | --- |
   | `지원하지 않는 이벤트: X (floor n)` | 4번 이벤트 조사 |
   | `맵 형식 version N은 미검증입니다` | 5번 형식 조사 |
   | Hold·Pause·FreeRoam·Checkpoint·pitch 등의 조건 문구 | 그 조건의 게임 근거를 확인해 범위를 넓힐 수 있는지 봅니다 |
   | `Error`(JSON·필드 검증 실패) | 파일 손상입니다. 파서는 추측 복구를 하지 않습니다 |

4. **이벤트 조사**
   - 사본에서 그 이벤트의 JSON을 그대로 봅니다(`README.md`의 사본 절차).
   - 게임 DLL에서 두 가지를 확인합니다.
     - `LevelEventType` 번호가 `scnGame.ApplyEventsToFloors`/`ApplyCoreEventsToFloors`에서 비교되는지
     - 타이밍 필드(`entryangle`·`exitangle`·`isCCW`·`midSpin`·`speed`·`extraBeats`·`holdLength`·`numPlanets`)에 쓰는지
   - 쓰지 않으면 `VisualEvents`에 근거 주석과 함께 넣습니다.
   - 쓰면 IL에서 공식을 확정하고, 확인된 좁은 조건만 지원하며 나머지는 `Unsupported`로 둡니다.
5. **형식 조사**
   - 게임이 레벨 `version`을 읽는 곳은 네 군데뿐입니다: `LevelData.LoadLevel`(7일 때만 `"enabled"`→`"active"`), `scrLevelMaker.MakeLevel`(9, 에디터), 장식 컴포넌트 이전(12 이하), 곡 목록(19 초과).
   - 그 형식의 실제 맵이 이미 검증한 이벤트·필드만 쓰는지 확인한 뒤 `AdofaiTimelineParser.FormatVersions`에 넣습니다.
6. **수정 뒤**: `README.md`의 "고친 뒤"를 따릅니다.

## 해결 기록 (날짜순)

**이 절은 해결한 날짜순(오래된 것부터)으로 쌓습니다. 새 사례는 맨 아래에 추가합니다** — 제목은 `### 날짜 · 곡 이름 (Workshop id, 앱 버전) — 한 줄 요약`이고, 본문에는 증상, 캐시에 적힌 이유 문구, 원인, 게임 근거(IL 덤프 파일 이름), 해결, 검증 결과를 아래 예시와 같은 형식으로 남깁니다. 같은 곡이 여러 번 제보되면 그 곡의 항목 안에 날짜순으로 줄을 더합니다.

### 2026-10-04 · cosMo@暴走P - THE END OF HATSUNE MIKU (3011005213, 앱 0.9.2) — MultiPlanet과 미드스핀 위의 Hold, 그리고 아직 남은 연속 Hold

**증상**: 곡 목록에 `Unsupported`, `load -force`로도 열리지 않음(형식 13은 이미 검증된 형식이라 강제 로드가 바꿀 것이 없음).

**캐시에 적힌 이유**(차례로 드러남 — 문서 맨 위에 적힌 대로 하나를 고치면 다음 것이 나옵니다):

1. `지원하지 않는 이벤트: MultiPlanet (floor 310).`
2. `Hold는 다음 타일이 일반 타일인 중간 타일에서만 지원합니다 (floor 644).`
3. `연속된 Hold는 지원하지 않습니다.` ← **아직 남아 있습니다**

**1. MultiPlanet** — 새 이벤트가 아니라 일부러 미지원이던 것입니다. 게임 근거:

- `scrLevelMaker.CalculateFloorEntryTimes`와 `CalculateSingleFloorAngleLength`가 `scrFloor.numPlanets`를 읽어 `scrMisc.GetInverseAnglePerBeatMultiplanet`에 넘깁니다(`.local/il-CalculateFloorEntryTimes.txt`). **타이밍 필드입니다.**
- 그 함수는 `π(n−2)/n`이고 게임의 π는 `3.1415926`입니다(`.local/il-multiplanet.txt`). n=2면 0이라 기존 맵은 아무 영향이 없습니다.
- 회전을 재기 전 진입각에 `inv × (isCCW ? −1 : 1)`을 더하고, 회전 자체를 같은 부호로 재므로 **방향이 상쇄되어 회전량만 그만큼 길어집니다**(세 행성 = 60°).
- 미드스핀 타일은 `shift = 0`, 미드스핀 **다음** 타일은 `shift −= (2π + inv) × dir`인데 게임의 2π가 `6.28318548202515`라 360°보다 아주 조금 큽니다 — 한 바퀴 여유(1e-6 rad)가 볼 수 있는 차이라 그대로 옮겼습니다.
- `numPlanets`는 `scnGame.ApplyEventsToFloors`가 각 타일의 이벤트를 다 본 뒤에 쓰므로 **`speed`처럼 뒤 타일로 이어집니다**(`.local/il-apply-events.txt`). 값은 `PlanetCount { TwoPlanets = 2, ThreePlanets = 3 }`이고 게임이 2~3으로 clamp합니다.

**2. 미드스핀 위의 Hold**(floor 644, duration 0, 다음 타일 645가 999) — `duration 0`이라 더해지는 시간은 없고 **노트 모델만** 문제였습니다. 게임 근거: `scrPlayer.Hit`이 밟은 타일이 미드스핀이면 `midspinInfiniteMargin`을 켜고(그래서 각도 여유 검사를 건너뜀), `scrPlayer.UpdateHoldKeys`가 그 플래그를 보고 **곧바로 한 번 더 `Hit`**을 합니다(`.local/il-player-hold.txt`·`il-player-hit.txt`). 즉 잡고 있는 키가 미드스핀을 그대로 넘어갑니다. 그래서 떼는 자리를 "다음 타일"이 아니라 **다음 누르는 타일**로 바꿨습니다(미드스핀은 노트가 아님).

**3. 남은 것 — 연속된 Hold**: 이 맵은 29·30·31·32처럼 **붙어 있는 Hold가 21쌍** 있습니다(전체 86개, duration은 0이 56개·1이 27개). 타이밍은 타일마다 따로 더해지므로 영향이 없지만, 한 노트가 "떼면서 동시에 잡기"가 되어 **지금 노트 모델로는 표현되지 않습니다**. 게임이 중간 타일에서 다시 누르게 하는지(`scrPlayer.UpdateHoldBehavior`의 `_nextTileIsHold`·`strictHolds`·`holdCompletion`)를 확인해야 모델을 정할 수 있어, **추측하지 않고 남겨 두었습니다**(안전 규칙 7). → **2026-10-04에 해결**했습니다: `UpdateHoldKeys`가 `_nextTileIsHold`이면 `holdCompletion`을 기다리지 않고 대기 중인 키 입력을 소비하므로 그 자리에서 **다시 누르는 것**이 맞고, 새 `HitKind.HoldChain`으로 표현합니다. 이 문서 맨 아래 항목을 보세요.

**검증**: `build.ps1 -Test` 226/226. 세 행성 타이밍과 미드스핀 위 Hold는 각각 테스트로 고정했고, 원본 맵은 SHA-256 `3031614795F6…83EA`와 수정 시각이 그대로임을 작업 전후로 확인했습니다. 캐시가 바뀌므로 **Map Sync가 필요합니다**.

### 2026-09-17 · cardboard box - He He He `low effect.adofai` (감사) — 일부러 남김

- 형식 version 6에 `pitch` 200입니다. pitch가 100이 아닌 타이밍은 검증하지 않았습니다.
- **사용자 결정으로 손대지 않습니다.** `load -force`로도 불러와지지 않습니다. 모든 형식에서 pitch 이유로 막히기 때문입니다.

### 2026-09-22 ~ 09-23 · Kubbi & Jonas Dam - Up In My Jam (Workshop 3012378858, 앱 0.6.5 · 0.6.7 · 0.6.8) — 세 번의 제보

| 날짜 · 앱 | 이유 | 원인과 해결 |
| --- | --- | --- |
| 2026-09-22 · 0.6.5 | `Hold는 1 이상의 정수 duration만 지원합니다 (floor 116)` | `CalculateFloorEntryTimes`는 `holdLength > 0`일 때만 시간을 더하고, `scrPlayer.UpdateHoldBehavior`는 `holdLength > -1`이면 hold로 처리합니다. 그래서 duration 0은 추가 시간 없이 다음 hit에서 떼는 롱노트입니다(파서 v16). |
| 2026-09-23 · 0.6.7 | `지원하지 않는 이벤트: FreeRoam (floor 206)` | 아래 FreeRoam 항목 |
| 2026-09-23 · 0.6.8 | "여전히 불가능" | 캐시 `ParserVersion`이 `adofai-v17`이었습니다. 0.6.7 수정은 Debug에만 있었고 사용자는 0.6.6 Release를 실행 중이었습니다(앱이 출력 폴더를 잠가 Release 빌드 실패). **코드 문제가 아니었습니다.** 해결: 앱을 닫고 Release를 빌드한 뒤 Map Sync. |

0.6.7의 FreeRoam 해결(파서 v18):
- 근거(`.local/inspect-freeroam.ps1`, `il-update-freeroam.txt`): `ApplyCoreEventsToFloors`(이벤트 44)는 다음 타일이 있고 duration이 정수 2 이상일 때 `extraBeats += max(a, duration − a)`를 더합니다(a는 그 타일의 회전 박). `scrController.UpdateFreeroam`은 구간이 끝나면 행성을 경로로 되돌리고, 다음 타일은 평소처럼 칩니다.
- 지원 범위는 **자동 타일에서 시작하는 구간**뿐입니다. 격자 안의 움직임은 경로의 hit가 아니라 노트로 보여 줄 수 없기 때문입니다.
- 회전은 180°·360°만 받습니다. 게임의 `angleLength`가 회전 반전(Twirl)을 반영하기 전 값일 수 있어서입니다.
- floor 205의 Hold는 다음 타일이 자동이라 게임이 떼기 판정(`Hit`·`Die`)을 건너뜁니다(`UpdateHoldBehavior`의 `nextTileIsAuto` 분기, `.local/il-hold-behavior.txt`). 그래서 누르기만 하는 단타로 표시합니다.

교훈: **이유 문구보다 `ParserVersion`을 먼저 봅니다.** 한 이유를 고친 뒤에는 사본으로 곡 전체를 파싱해 다음 이유가 없는지까지 확인합니다.

### 2026-09-23 · ToTheMoon - 정령의 성 (Workshop 2494144385, 앱 0.6.8) — 형식 version 3

- 이유: `맵 형식 version 3은 미검증입니다`.
- 맵 내용: `pathData`, SetSpeed 40개, Twirl 235개, 연출 이벤트(MoveCamera·SetFilter·Flash·ColorTrack·CustomBackground·SetHitsound·MoveTrack·AnimateTrack)뿐입니다. 이미 검증한 형식 2 맵(S3RL - Hentai)과 같은 구성입니다.
- 근거: 게임에는 형식 3에만 적용되는 분기가 없습니다(진단 순서 5번). 2·4와 같은 경로를 탑니다.
- 해결: `FormatVersions`에 3을 넣었습니다(파서 v19). 한 곡 대조 결과 977 hit, 최대 0.00033ms입니다.

### 2026-09-23 · Plum - Wake Up, Piano! (Workshop 2128998212, 앱 0.6.10) — 형식 version 6

- 캐시 이유는 `맵 형식 version 6은 미검증입니다`. 캐시와 코드가 모두 파서 v19여서 오래된 캐시 문제가 아니었습니다.
- 원본은 angleData 975개(중간 회전 16개), BPM 256, pitch 100, Linear이며 SetSpeed 113개·Twirl 262개와 기존에 검증한 연출 이벤트만 사용합니다.
- 근거: `docs/adofai-parser.md`의 형식별 게임 코드 조사와 `ADOFAI.LevelData.LoadLevel` ReflectionOnly 재확인(`.local/il-wake-format-loadlevel.txt`). LoadLevel의 속성 이전은 version 7에만 적용되며, 6 전용 타이밍 변환은 없습니다.
- 해결: 형식 6을 정식 지원하고 파서를 `adofai-v20-format6`으로 올렸습니다. 원본 version·타이밍 공식은 그대로입니다. pitch 200과 알 수 없는 타이밍 이벤트는 계속 거부하므로 He He He low effect도 계속 미지원입니다.
- 회귀 자료: `tests/Visualizer.Tests/Fixtures/WakeUpPiano.json`은 원본 바이트 그대로인 테스트 전용 사본입니다. 테스트는 앱 저장소를 읽지 않습니다. 원본 SHA-256은 `9439F5089F44566F51FDC5E5C61B5269145F2B02AA52B1E883267D917E49C9D8`입니다.
- 검증: 수정 전 새 테스트가 형식 6 차단으로 실패(186/187), 수정 후 Debug 187/187 통과. 곡의 959 hit와 기본 설정의 959개 노트 준비를 확인했습니다. 독립 계산 대조·외부 맵 전체 회귀·실제 플레이 측정은 실행하지 않았습니다.
- 적용: 새 Release로 실행한 뒤 **Map Sync**가 필요합니다. 기존 캐시는 직접 수정하지 않았습니다.
- 최종 확인: Debug·Release 모두 경고·오류 0, 187/187 통과. 원본 SHA-256과 수정 시각이 작업 전후 동일하며 테스트 사본도 원본과 바이트 단위로 같습니다.

### 2026-10-04 · 불러오지 못하던 곡 전부 (2850594034 · He_He_He low effect · 3011005213, 앱 0.9.2) — 쉼표·형식 9·Toggle·ScaleMargin·직접 조작 FreeRoam·연속 Hold·pitch

**증상**: 사용자 제보 "일부 곡이 불러와지지 않습니다". 캐시 57항목 중 Unsupported 2개와 Error 1개였습니다.

**한 곡에서 벽이 일곱 번 나왔습니다**(문서 맨 위의 경고대로, 하나를 고치면 다음 것이 드러납니다). 차례와 근거:

1. `맵 데이터 검증 실패: '"' is invalid after a value ... LineNumber: 1399` — `actions` 배열의 `]` 다음에 **쉼표가 없고** 바로 `"decorations"`가 옵니다.
   근거: 게임은 레벨을 `GDMiniJSON`으로 읽습니다(`ADOFAI.LevelData.LoadLevel` → `RDFile.ReadAllText` → `GDMiniJSON.Json.Deserialize`, `.local/inspect-level-json.ps1`). 그 파서는 **어느 메서드에도 `throw`가 없고**(`.local/inspect-minijson.ps1`: 모든 메서드 `throw=0`, 실패는 null 반환뿐) `ParseObject`는 다음 멤버를 **switch의 default**에서 `ParseString`으로 읽습니다(`.local/inspect-minijson-loop.ps1`). 즉 쉼표는 있으면 건너뛰고 없어도 읽습니다.
   해결: `AdofaiJson.NormalizeStrings`가 메모리에서만 쉼표를 넣습니다. 배열 안에서는 어떤 값 앞에도, **객체 안에서는 이름(문자열) 앞에만** 넣습니다 — 게임도 숫자를 이름으로 읽지 못해 null을 돌려주므로, 게임이 못 읽는 파일은 여기서도 Error로 둡니다.
2. `맵 형식 version 9은 미검증입니다` — 근거: version 9 분기는 `scrLevelMaker.MakeLevel`의 **에디터 마이그레이션 하나뿐**입니다(`.local/il-dump.ps1 -Type scrLevelMaker -Method MakeLevel`): `LevelData.shouldTryMigrate` + `ADOBase.editor`가 있을 때 + version 9일 때, 진입·진출 각이 90° 모듈로 같고 한 바퀴가 아닌 타일의 Pause duration에 1을 더합니다. 플레이 중에는 에디터가 없어 돌지 않으므로 **파일의 duration 그대로** 재생됩니다. `LevelData.version`을 읽는 곳은 그 밖에 `LoadLevel`(7의 문자열 치환)·`Copy`·장식 컴포넌트 이전뿐입니다(`.local/inspect-version-refs.ps1`).
3. `AutoPlayTiles는 enabled 값이 필요합니다 (floor 506)` — 파일은 `"enabled": "Enabled"`(문자열)입니다. 근거: `LevelEvent.Decode`는 PropertyType 1 속성에서 값이 문자열이면 `value == "Enabled"`를 bool로 넣고(`.local/il-levelevent-decode.txt` IL_0277–02B0), `LevelEvent.GetBool`은 `Get<bool>(key, false)`라 그 bool만 돌려줍니다. 해결: 같은 규칙의 `Toggle` 헬퍼로 읽습니다(없는 속성은 이벤트 정의의 기본값이라 아직 거절).
4. `지원하지 않는 이벤트: ScaleMargin (floor 1050)` — 근거: 타이밍 패스(`ApplyCoreEventsToFloors`)의 분기는 2·3·33·43·44와 48/52/53/54뿐이라 51은 default로 가고, 시각 패스는 `scrFloor.marginScale`만 씁니다. `CalculateFloorEntryTimes`가 읽는 필드는 `isCCW`·`midSpin`·`entryangle`·`exitangle`·`holdLength`·`numPlanets`·`extraBeats`·`speed`·`prevfloor`뿐입니다(`.local/il-CalculateFloorEntryTimes.txt`). 판정 폭만 바꾸므로 `VisualEvents`에 넣었습니다.
5. `직접 움직이는 FreeRoam(자동 타일이 아닌 구간)은 지원하지 않습니다` — floor 266·267에 각 16박 FreeRoam이 있고 그 구간은 auto가 아닙니다. 타이밍은 자동 구간과 **같습니다**(코어 패스의 이벤트 44는 auto 플래그를 보지 않음). 해결: 구간을 그대로 지원하고, 구간 동안은 노트를 두지 않고 "미니게임" 문구만 띄우며 구간을 시작하는 타일은 평소처럼 눌립니다. 경고 문구에 자동/직접 조작을 구분해 적습니다.
6. `연속된 Hold는 지원하지 않습니다` (3011005213, 21쌍) — 근거: `CalculateSingleFloorAngleLength`가 `holdLength > 0`인 타일에만 `holdLength × 2 × π`를 더하므로 **이웃과 무관**하고, 사이의 hit은 `scrPlayer.UpdateHoldKeys`가 `_nextTileIsHold`(= `currFloor.nextfloor.holdLength > -1`)일 때 `holdCompletion`을 기다리지 않고 **대기 중인 키 입력을 소비**합니다(`keyTimes.RemoveAt(0)`). 즉 그 자리에서 다시 누릅니다. 해결: `HitKind.HoldChain`(앞 롱노트를 떼면서 다음을 누르는 hit, 눌리는 종류)을 더하고, 롱노트가 끝나는 **그 순간의** 같은 레인 노트를 허용했습니다(`SimultaneousNotePlanner.Add`·`NoteGenerator`의 `<=` → `<`). 자동 타일에서 끝나는 연속 Hold는 보여 줄 떼기가 없어 아직 미지원입니다.
7. `100% 이외의 pitch 타이밍은 아직 지원하지 않습니다` (He_He_He low effect, pitch 200) — 근거: `scrConductor.SetupConductorWithLevelData`가 `song.pitch = levelData.pitch × 0.01`(게임에서는 `GCS.currentSpeedTrial`을 더 곱함)로 두고, `CalculateFloorEntryTimes`는 `entryTime`(박 기준) 옆에 **`entryTimePitchAdj = entryTime / song.pitch`**를 함께 둡니다(`.local/inspect-pitch.ps1`, `.local/il-CalculateFloorEntryTimes.txt`). 우리 시계는 실제 시간이므로 **모든 시각을 pitch로 나눕니다**(200이면 절반). `get_pitch`가 int로 언박싱하고 0 이하면 곡이 멈추므로 정수·양수만 받습니다.

**해결**: 파서 `adofai-v23-pitch-chained-holds`, 지원 형식에 **9**를 더했습니다. **새 Release로 실행한 뒤 Map Sync가 필요합니다**(파서 버전이 바뀌어 캐시를 다시 읽어야 합니다).

**검증**: Debug·Release 모두 경고·오류 0, 233/233 통과. 세 곡의 **사본**을 `.local/tmp/check`에 두고 `-ReadOnlyMapRoot` 감사까지 통과했습니다(234/234, 파싱·준비·체크포인트·렌더링). 작업 전후 원본 SHA-256(`48792DF3…` / `30316147…` / `83489602…`)과 수정 시각이 같고 사본은 지웠습니다. **게임 자체와의 대조(`link tiles`)는 하지 않았습니다** — 특히 형식 9의 Pause 9개, 직접 조작 FreeRoam, pitch는 그 대조로 확인해야 합니다. pitch≠100인 곡은 모드가 박 기준 `entryTime`을 보내므로 타일 대조·따라가기가 맞지 않습니다(확인되지 않은 조합).

## 마지막 수단: `load -force` (앱 0.6.8, 사용자 규칙)

- 콘솔 `load -force <곡>`은 Unsupported 곡에만 동작하고, 위험하므로 **항상 [Y/n]을 묻습니다**.
- 읽는 순서: 곡의 형식보다 낮은 검증 형식을 가까운 것부터 시도하고, 없으면 높은 것을 시도합니다(5→4→3→2→8…, 1→2, 20→19). 검증되지 않은 번호는 건너뜁니다. 처음 읽히는 형식이 이깁니다.
- 결과 처리
  - 성공하면 캐시 항목에 `ForcedFormatVersion`이 남고, 곡 목록에 주황 `forced`가 붙습니다. 같은 파일인 동안은 Map Sync 뒤에도 forced로 유지됩니다.
  - 모든 형식이 실패하면 캐시를 건드리지 않습니다. 콘솔은 영어로 알리고, 메인 화면은 파서의 이유를 보여 줍니다.
- 지금 파서에는 형식 번호에 따라 달라지는 규칙이 없습니다. 그래서 강제 로드는 **형식 검사만 건너뛰는 것**과 같고, 이벤트나 조건 때문에 막힌 곡은 모든 형식에서 실패합니다.
- 에이전트는 `-force`를 해결책으로 권하기 전에 위 진단으로 **정식 지원**을 먼저 시도합니다. 정령의 성도 `-force`가 아니라 형식 3 정식 지원으로 풀었습니다.
