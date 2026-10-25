# ADOFAI 파서와 실제 맵 호환 범위

`AdofaiTimelineParser`(`adofai-v27-any-first-direction`)는 JSON 원문만 받고 파일 쓰기 기능이 없습니다. Map Sync가 읽기 전용 소스로 원문을 넘기고 결과만 앱 캐시에 저장합니다. hit마다 게임 타일(floor) 번호를 함께 저장합니다(구간 시작용). 게임 앱 버전과 맵 형식 버전은 구분합니다([환경 대응](environment-compatibility.md)). 모든 `.adofai`를 지원한다는 뜻은 아니며, 범위 밖 이벤트는 규칙과 근거·테스트를 먼저 추가해야 합니다(AGENTS 안전 규칙 7).

## 지원 범위

- 형식 version 2/3/4/6/8/9/10/11/13/15/18/19, 첫 방향은 아무 각도(첫 타일 중간 회전 제외), 두·세 행성, 기본 planetEase=Linear.
- `angleData`의 일반 각도는 음수·다회전을 360도 기준으로 정규화하고 정확히 999만 중간 회전입니다. 구형 `pathData`의 절대 각도 문자와 `!`(중간 회전)를 지원하고 상대 각도·알 수 없는 문자는 거부합니다.
- 구형 JSON 문자열 안의 줄바꿈·제어 문자, 생략된 쉼표는 메모리에서만 보정합니다(`AdofaiJson`). 문자열 밖 구문 오류는 복구하지 않습니다.
- `SetSpeed`(Bpm/Multiplier), `Twirl`, `Pause`(유한한 0 이상 duration, angleCorrectionDir는 Backward·None·Forward(이름이나 -1·0·1) 또는 생략).
- **시청각 이벤트**는 타이밍에서 제외합니다: `ScalePlanets`, `PlaySound`, `SetHitsound`, `AddDecoration`, `PositionTrack`, `SetText`, `ScreenTile`, `ScreenScroll`, `SetParticle`, `SetDefaultText`, `Hide`, `ScaleRadius`, `SetObject`, `AddObject`, `HallOfMirrors`, `SetFrameRate`, `EmitParticle`, `SetHoldSound`, `AddText`, `ScaleMargin` 등. 효과 자체는 그리지 않습니다.
- **Hold**: 0 이상의 정수 duration, 중간 타일. 다음 hit까지 `duration × 2`박을 더하고(0은 더하지 않음) 다음 hit는 떼는 지점(`HitKind.HoldEnd`, 노트 아님). 다음 타일이 미드스핀이면 다음 **누르는** 타일에서 뗍니다. 다음 타일이 자동이면 누르기만 하는 단타(`Tap`). **연속 Hold**는 사이 hit가 앞을 떼며 다음을 누르는 `HitKind.HoldChain`입니다. 거절: 음수·소수·누락 duration, 시작/마지막/중간 회전 타일, 자동 타일 위의 Hold, 자동 타일에서 끝나는 연속 Hold, 롱노트 도중 체크포인트·구간 시작.
- **AutoPlayTiles**: `enabled`(bool 또는 `"Enabled"`)가 다음 AutoPlayTiles까지 이어지며, 켜진 타일 hit는 `HitKind.Auto`(노트 아님).
- **FreeRoam**(자동·직접 조작 모두): 다음 hit는 회전 a박 + `max(a, duration − a)`박 뒤이고, 구간 시각을 `ParsedTimeline.Sections`로 저장해 "미니게임"을 표시합니다. 구간 안 자유 이동은 hit가 아닙니다. 조건: 2 이상 정수 duration, 시작/마지막/중간 회전 타일 아님(다음 타일도), angleCorrectionDir Backward, 한 타일에 하나, 같은 타일에 Hold·Pause 없음, 회전 180°·360°. 거절: FreeRoam 타일로의 체크포인트 재시작, `FreeRoamTwirl`·`FreeRoamRemove`·`FreeRoamWarning`.
- **MultiPlanet**: 행성 수는 `speed`처럼 뒤 타일로 이어지고, 진입각을 `π(n−2)/n`만큼 옮겨 다음 행성이 그만큼 가까워지므로 그 구간 모든 회전이 **짧아집니다**(3행성 = 60°, 180° 타일은 120°, 방향과 무관). 미드스핀 다음 타일은 한 바퀴 뒤로 옮겨 60°가 상쇄됩니다. 게임 자체 타일 시각(THE END OF HATSUNE MIKU, 링커 파이프)과 1450개 hit가 1ms 안에서 같습니다. 첫 타일부터 3행성·4행성 이상은 거절.
- **pitch**: 0보다 큰 정수, 모든 실제 시각을 pitch로 나눕니다. pitch≠100이면 게임 링크의 타일 대조가 맞지 않습니다(모드가 보내는 `entryTime`은 나누기 전).
- `SetConditionalEvents`: 판정 결과별 태그(`…Tag`)가 가리키는 이벤트가 모두 시청각일 때만. "태그 없음"은 `NONE`·`无`·`無`·`AUCUN`·`ŽÁDNÝ`·`NICHTS`뿐(한국어 `없음`은 실제 태그).
- `RepeatEvents`: `eventTag`가 있는 대상 중 태그가 맞는 이벤트가 모두 비타이밍일 때만. 공백으로 나누되 빈 항목을 버리지 않습니다(빈 태그 = 빈 `eventTag`).
- 에디터에서 끈 이벤트(`"active": false`, bool일 때만)는 타이밍·체크포인트·자동 타일 계산에서 뺍니다. 태그 검사는 꺼진 이벤트도 봅니다.
- 곡·아티스트 메타데이터를 읽고 `<color>` 등 표시 태그를 제거합니다.
- **강제 로드**(콘솔 `load -force`): Unsupported 곡을 [Y/n] 뒤 검증된 다른 형식으로 읽습니다(`ParseForced`: 낮은 형식을 가까운 것부터, 없으면 높은 것). 지금은 형식별 규칙이 없어 형식 검사만 건너뛰는 것과 같고, 캐시 `ForcedFormatVersion`·목록 `forced`로 구분합니다.
- 알 수 없는 이벤트, 타이밍 이벤트의 0이 아닌 angleOffset, 위 조건 밖 조합은 Unsupported, 잘못된 JSON·필드는 Error.

## 계산 근거

- `시간(ms) = 회전 각도/180 × 60000/BPM`. floor 이벤트는 그 floor에서 다음 floor로 가는 구간에 적용합니다. 중간 회전은 방향을 뒤집고 시간을 만들지 않으며, 시작 floor 0과 중간 회전 floor는 hit가 아닙니다.
- 회전량이 0 또는 한 바퀴와 1e-6 rad 이내면 한 바퀴(`scrLevelMaker.CalculateSingleFloorAngleLength`: `|각도| ≤ 1e-6` 또는 `!(|각도| < 6.28318430717959)`이면 2π, 중간 회전이면 0).
- Pause는 그 구간에 `duration × 60000 / BPM`을 더합니다(`scnGame.ApplyCoreEventsToFloors`, `scrLevelMaker.CalculateFloorEntryTimes`). countdownTicks는 노트가 아닙니다. 방향(`angleCorrectionDir`)은 시각과 무관합니다: `ApplyCoreEventsToFloors`가 `scrFloor.angleCorrectionType`에 넣을 뿐 `CalculateFloorEntryTimes`는 읽지 않고, `scrPlanet.HandlePause`는 `lastHit`·`snappedLastAngle`을 방향과 상관없이 정한 뒤 이 값을 보이는 각도 `TweenCosmeticAngle`에만 곱합니다(읽는 곳은 이 둘과 `MakeFreeroamGrid`뿐, `.local/il-handle-pause.txt`·`il-angle-correction-refs.txt`).
- 공개 구현 대조: [SharpFAI FloorTimingCalculator](https://github.com/adofaiex/SharpFAI/blob/main/SharpFAI/Util/FloorTimingCalculator.cs), [그 테스트](https://github.com/adofaiex/SharpFAI/blob/main/SharpFAI.Tests/TestGetNoteTimesNew.cs), [ADOFAI map combiner](https://github.com/Lipcu-pku/ADOFAI_map_combiner/blob/main/map%20combiner.py). 그 코드나 패키지는 도입하지 않았습니다.
- 아래 근거는 설치된 게임 파일을 ReflectionOnly로 읽어 확인했습니다(실행·복사하지 않음, 덤프·스크립트는 `.local/`).

### 게임 파일 근거

- **형식 version**: 레벨 `version`을 읽는 곳은 `LevelData.LoadLevel`(7일 때만 `"enabled"`→`"active"`), `scrLevelMaker.MakeLevel`(에디터에서 9일 때만 조건에 맞는 Pause duration +1 — 플레이에는 영향 없음), `LevelData.Copy`, `LevelEvent.Decode`의 장식 이전(12 이하, `scrLockToCamera`만), 곡 선택 목록(`LevelDataCLS.Decode`, 19 초과)뿐입니다. 그래서 지원 형식 사이에 타이밍 변환이 없습니다. version 19 설정(`legacyTween`·`legacyFlash`·`legacyCamRelativeTo`·`disableV15Features`)은 연출·저장에만 쓰입니다.
- **첫 방향**: `scrLevelMaker.InstantiateFloatFloors`가 floor 0의 entryangle을 3π/2로, 각 floor의 exitangle을 (90° − 방향)×π/180으로, 다음 floor의 entryangle을 (exitangle + π) mod 2π로 둡니다(중간 회전은 exitangle = entryangle). 3π/2는 0° 방향 뒤 타일의 진입각과 같으므로 첫 회전도 "이전 방향 0°"로 다른 타일과 같은 식이고, 첫 방향은 첫 hit의 시각만 바꿉니다. 첫 타일이 중간 회전인 맵은 검증하지 않았습니다. 사례: S3RL - play it loud(345°).
- **타이밍 필드를 쓰는 곳**: `scrFloor`의 entryangle/exitangle/isCCW/midSpin/speed/extraBeats/holdLength/numPlanets/entryTime에 쓰는 메서드는 `scnGame.ApplyEventsToFloors`(내부 `ApplyCoreEventsToFloors`), `scrLevelMaker`의 생성·시간 계산, `scrFloor` 초기화, 프리롬·메뉴·특정 공식 레벨 코드뿐입니다. `ApplyCoreEventsToFloors`가 비교하는 `LevelEventType`은 1~3(SetSpeed/Twirl/Checkpoint), 33(Hold), 43(MultiPlanet), 44(FreeRoam), 48~54(Pause 등)이고, `CalculateFloorEntryTimes`는 위치를 읽지 않습니다. 시청각 이벤트 번호(SetText 21, AddText 20, HallOfMirrors 27, SetHoldSound 34, SetConditionalEvents 35, ScreenTile 36, ScreenScroll 37, AddObject 58, SetObject 59, SetDefaultText 60, SetFrameRate 61, SetParticle 63, EmitParticle 64, ScaleMargin 51 등)는 이 비교에 없습니다. Hide 50은 `hideJudgment`·`hideIcon`만, ScaleRadius 52는 `radiusScale`만 씁니다.
- **enum 값**: `LevelEvent.Decode`는 enum 속성이 숫자면 `Enum.ToObject`, 문자열이면 `Enum.Parse`. `AngleCorrectionDirection`은 Backward=-1, None=0, Forward=1입니다. Pause는 셋 다, FreeRoam은 격자에 쓰이므로 -1만 허용합니다. PropertyType 1(Toggle)은 `"Enabled"`만 true로 바꿔 넣고 `GetBool`이 읽습니다.
- **pathData**: `ADOFAI.FloorHelper.TryGetPathInDegrees`/`MigratePathData`. 15도 간격 절대 문자 순서 `RpJEToUqGQHWLxNZFVDYBCMA`, `!`는 999, 문자 하나당 floor 하나.
- **Hold**: `ApplyCoreEventsToFloors`(33)는 다음 타일이 있고 duration ≥ 0이면 `holdLength = duration`, 아니면 -1. `CalculateFloorEntryTimes`는 `holdLength > 0`일 때만(IL `ble.s`) 회전 시간 뒤에 `holdLength × 2 × 한 박`을 더하고, `scrPlayer`는 `holdLength > -1`이면 hold 입력으로 처리하므로 duration 0은 추가 시간 없이 다음 hit에서 떼는 롱노트입니다. `UpdateHoldBehavior`는 다음 타일이 자동이면 떼기 판정을 건너뜁니다. 미드스핀에서는 `scrPlayer.Hit`이 `midspinInfiniteMargin`을 켜고 `UpdateHoldKeys`가 한 번 더 `Hit`. 연속 Hold는 `UpdateHoldKeys`가 `_nextTileIsHold`(`nextfloor.holdLength > -1`)이면 `holdCompletion`을 기다리지 않고 대기 중인 키 입력을 소비합니다.
- **AutoPlayTiles**: `ApplyEventsToFloors`가 상태를 false로 시작해 이벤트마다 바꾸고 각 타일 `auto`에 복사하므로 다음 이벤트까지 이어집니다. `scrPlayer.get__nextTileIsAuto`가 읽습니다.
- **FreeRoam**: `ApplyCoreEventsToFloors`(44)는 다음 타일이 있고 duration ≥ 2일 때 `a = angleLength / π`, `extraBeats += max(a, duration − a)`. `angleLength`는 회전 반전 전 값일 수 있어 방향과 무관한 180°·360°만 허용합니다. auto 플래그는 보지 않으므로 직접 조작도 타이밍이 같습니다. 구간 끝은 `scrController.UpdateFreeroam`이 `다음 타일 entryTime − freeroamEndEarlyBeats`에 입력을 잠그는 것입니다.
- **MultiPlanet**: `CalculateFloorEntryTimes`가 회전을 재기 전에 `scrMisc.GetInverseAnglePerBeatMultiplanet(n) = π(n−2)/n`(게임 π `3.1415926`)만큼 진입각을 옮기고, 미드스핀 다음 타일에서 `6.28318548202515 + π(n−2)/n`를 뺍니다(모두 `isCCW ? −1 : 1`을 곱함, 미드스핀 타일 자신은 0). 회전은 `GetAngleMoved`가 같은 부호로 재므로 방향과 무관하게 짧아집니다 — 처음에는 길어진다고 잘못 읽었다가 게임이 보낸 타일 시각으로 바로잡음(2026-10-12).
- **pitch**: `scrConductor.SetupConductorWithLevelData`가 `song.pitch = pitch × 0.01`, `CalculateFloorEntryTimes`가 `entryTimePitchAdj = entryTime / song.pitch`. `get_pitch`는 int 언박싱이라 정수만.
- **쉼표 생략**: 게임은 `LevelData.LoadLevel` → `RDFile.ReadAllText` → `GDMiniJSON Json.Deserialize`로 읽고, 그 파서는 `throw`가 없고 다음 멤버를 switch default로 읽습니다. 그래서 배열에서는 모든 값 앞에, 객체에서는 이름 앞에만 쉼표를 넣습니다. 거꾸로 값 없이 놓인 쉼표(연달아 두 개, 괄호 바로 뒤)도 `ParseObject`·`ParseArray`가 COMMA 토큰(6)을 읽고 그냥 루프를 돌기 때문에 건너뛰므로, 메모리에서 뺍니다(PLUM MEGAMIX 2의 `"requiredMods": [],,`, `.local/il-minijson-object.txt`·`il-minijson-array.txt`).
- **에디터에서 끈 이벤트**: `ADOFAI.LevelEvent.active`를 타이밍 쪽에서 읽는 곳은 `ApplyEventsToFloors`뿐이고, 첫 루프에서 `active`가 거짓이면 건너뜁니다. `Decode`는 bool일 때만 그 값을 씁니다.
- **SetConditionalEvents**: 35를 비교하는 곳은 `ApplyEventsToFloors`의 필터 람다와 `scnGame.PrepVfx`뿐이고, `hasConditionalChange`와 판정별 FX 목록만 채웁니다. `RDUtils.noneTags`가 위 여섯 단어입니다.
- **RepeatEvents**: `ApplyEventsToFloors`는 이벤트에 `eventTag` 속성이 있을 때만 `Split(" ")`으로 맞춥니다(SetSpeed/Twirl은 대상 아님).
- **backup.adofai**: `scnEditor.SaveBackup`이 에디터 자동 저장으로 씁니다. `scnGame.GetWorldPaths`는 `main.adofai`를, `AdoPackageInstaller.FindLevelFile`·`scnEditor.FindAdofaiLevelOnDirectory`는 backup과 `.` 파일을 건너뜁니다. 그래서 `MapFileRules`는 같은 폴더에 다른 레벨 파일이 있으면 `backup.adofai`·`backup2.adofai`를 곡으로 올리지 않습니다.
- **헤어핀**(Search Party floor 752~768): 게임 IL은 출구 각도를 r4로 만들지만 런타임 정밀도는 IL로 정할 수 없어, 사용자가 게임에서 확인했습니다(모두 정박 2박). 파서의 degree 계산이 맞으며 기준 계산기도 double을 유지합니다(IL 근거가 아니라 실제 플레이 확인).

degree 단위의 독립 구현이며 게임의 부동소수점을 비트 단위로 복제하지는 않습니다. 독립 기준 계산기(`GameRuleTimingComparison`)가 hit 시각과 hit 종류를 따로 계산해 대조합니다([비교 근거](timing-comparison.md)). 게임 자체와의 대조는 게임 링크의 `link tiles`입니다.

원문 offset·countdown은 오디오 동기화 데이터라 레인 간격에 섞지 않습니다(첫 hit를 t=0으로 정규화).

## 예제 맵

- `.example/main.adofai`: Plum - R, 기본 BPM 180, 각도 729개(중간 회전 3), SetSpeed 88, Twirl 84, hit 726, 체크포인트 6, 첫 hit 333.333ms ~ 마지막 139000ms. SHA-256 `E7FB46B01C535DE9918EAF7A2121C0059C6891322F103D225693984DDB8A2D3A`. 음원 `R.ogg`는 없습니다.
- 예제 버튼은 이 파일을 읽기 전용으로 직접 파싱해 준비하며 곡 목록·캐시에 넣지 않습니다. 테스트가 원본 해시와 수정 시각 불변을 확인합니다.

## 외부 맵 검증

```powershell
.\build.ps1 -Configuration Release -Test -ReadOnlyMapRoot '<맵 루트>'   # 사용자가 요청할 때만
```

지정한 루트만 읽고 파일의 SHA-256·수정 시각이 전후 같은지 검사합니다. 기본 테스트는 외부 맵을 읽지 않습니다. 파서가 바뀌면 앱에서 Map Sync를 다시 해야 합니다(이전 캐시는 자동 변환하지 않음).
