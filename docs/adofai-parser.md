# ADOFAI 파서와 실제 맵 호환 범위

## 지원 범위

`AdofaiTimelineParser` (`adofai-v6-practice-tiles`)는 JSON 원문만 받고 파일 쓰기 기능을 제공하지 않습니다. Map Sync가 읽기 전용 소스를 통해 원문을 전달하고, 결과만 전용 캐시에 저장합니다. v4는 형식 버전과 offset/countdownTicks/separateCountdownTime 안내 및 필드 검증을 추가했습니다. v5는 형식 version 8, 게임에 적용되는 `PositionTrack`, 빈 태그 `RepeatEvents`를 아래 근거로 허용했으며 타이밍 공식은 그대로입니다. v6은 [구간 시작](checkpoints.md)을 위해 hit마다 게임 타일(floor) 번호를 함께 캐시에 저장하며, hit 시각은 v5와 같습니다. 게임 앱 버전과 맵 형식 버전을 구분하며 [일반판 환경 대응](environment-compatibility.md)을 참고하세요.

- version 2/3/4/6/8/**9**/11/13/15/18/19의 검증된 이벤트 조합, 첫 방향=0도, 기본 두 행성, 0보다 큰 정수 pitch(100이 아니면 모든 시각을 그 값으로 나눔), 기본 planetEase=Linear. 형식 9의 유일한 version 분기는 에디터 마이그레이션이라 플레이에는 영향이 없습니다(`scrLevelMaker.MakeLevel`이 에디터에서만 조건에 맞는 Pause duration에 1을 더함).
- **형식 6(0.6.10, Plum - Wake Up, Piano!)**: 기존 형식별 게임 코드 조사와 LoadLevel 재확인에서 전용 타이밍 변환이 없고, 실제 맵이 기존 SetSpeed·Twirl·연출 이벤트만 사용함을 확인했습니다. 파서 `adofai-v20-format6`으로 지원하며 테스트 전용 사본으로 959개 노트 준비를 검증했습니다. pitch 200인 He He He low effect는 계속 미지원입니다. [해결 기록](debug-logs/unsupported-maps.md) 참조.
- `angleData`의 일반 각도는 음수와 다회전 값을 포함해 360도 기준으로 정규화합니다. 정확히 999인 값만 중간 회전입니다.
- 구형 `pathData`의 절대 각도 문자와 `!` 중간 회전. 상대 각도 문자와 알 수 없는 문자는 거부합니다.
- 구형 JSON 문자열 안의 실제 줄바꿈/제어 문자는 메모리에서만 escape합니다. 원본이나 맵 폴더에 변환 파일을 쓰지 않습니다. 문자열 밖 구문 오류나 잘못된 escape까지 임의 복구하지 않습니다.
- floor에서 즉시 적용되는 `SetSpeed`의 Bpm/Multiplier, `Twirl`.
- 일반 중간 타일의 유한한 0 이상 duration을 가진 `Pause`. angleCorrectionDir는 Backward(문자열 `"Backward"` 또는 숫자 `-1`) 또는 생략만 지원합니다.
- 명시한 시청각 이벤트만 타이밍 계산에서 제외. `ScalePlanets`, `PlaySound`, `SetHitsound`, `AddDecoration`, `PositionTrack`(editorOnly와 무관), `SetText`, `ScreenTile`, `ScreenScroll`, `SetParticle`, `SetDefaultText`, `Hide`, `ScaleRadius`, `SetObject`, `AddObject`, `HallOfMirrors`, `SetFrameRate`, `EmitParticle`, `SetHoldSound`, `AddText`를 포함합니다. 이 효과 자체를 렌더링하거나 소리를 재생하지는 않습니다.
- `Hold`(롱노트, 앱 0.1.1): 0 이상의 정수 `duration`을 가진 중간 타일에서, 다음 타일이 일반 타일일 때만. 다음 hit까지 `duration × 2`박을 더하고(duration 0은 더하지 않음, 앱 0.6.5), 그 다음 hit는 롱노트를 떼는 시점(`HitKind.HoldEnd`)이라 노트를 만들지 않습니다. 음수·소수·누락 duration, 시작/마지막/중간 회전 타일, 다음 타일이 중간 회전, 연속·중복 Hold, 자동 타일 위의 Hold, 롱노트 도중 체크포인트·구간 시작은 Unsupported(구간은 이유 표시)입니다. 다음 타일이 자동 타일이면 게임이 떼기를 보지 않으므로 누르기만 하는 단타(`Tap`)로 표시합니다(앱 0.6.7, Up In My Jam).
- `AutoPlayTiles`(자동 타일, 앱 0.1.1): `enabled` 값이 다음 AutoPlayTiles까지 이어지며, 켜진 타일의 hit는 게임이 치므로 `HitKind.Auto`로 표시하고 노트를 만들지 않습니다(사용자 결정, 토글 없음). hit 시각은 바뀌지 않습니다.
- `FreeRoam`(앱 0.6.7, Up In My Jam): **자동 타일에서 시작하는 구간만** 지원합니다. 구간의 시작·끝 시각은 `ParsedTimeline.Sections`로 캐시에 저장해 화면이 "미니게임"을 표시하는 데 씁니다(앱 0.6.11). 다음 hit는 FreeRoam 타일 hit에서 `duration`박 뒤(정확히는 회전 a박 + `max(a, duration − a)`박)이고, 구간 안에서 행성이 격자를 움직이는 동작은 경로의 hit가 아니라 노트를 만들지 않습니다. 2 이상의 정수 duration, 시작/마지막/중간 회전 타일이 아니고 다음 타일도 중간 회전이 아닐 것, angleCorrectionDir Backward, 한 타일에 하나, 같은 타일에 Hold·Pause 없음, 회전이 180°·360°(회전 방향과 무관한 경우)일 것을 요구합니다. 직접 움직이는 FreeRoam(자동 타일이 아님), FreeRoam 타일로의 체크포인트 재시작, `FreeRoamTwirl`·`FreeRoamRemove`·`FreeRoamWarning`은 Unsupported입니다.
- `SetConditionalEvents`(앱 0.1.9): 판정 결과별 태그(`perfectTag`·`lossTag` 등 이름이 `Tag`로 끝나는 속성)가 가리키는 이벤트가 모두 시청각 이벤트일 때만 허용합니다. 게임이 "태그 없음"으로 보는 값은 `NONE`·`无`·`無`·`AUCUN`·`ŽÁDNÝ`·`NICHTS`뿐이므로(RDUtils.noneTags), 한국어 `없음` 같은 문구는 실제 태그로 취급합니다. RepeatEvents와 같이 공백으로 나눠 맞춰 보며, 타이밍·체크포인트 이벤트가 걸리면 Unsupported입니다.
- RepeatEvents는 `eventTag` 속성이 있는 대상 중 태그가 맞는 이벤트가 모두 비타이밍 이벤트일 때 허용합니다. 게임과 같이 공백으로 나누되 빈 항목을 버리지 않으므로 빈 태그는 빈 `eventTag`와 맞는 것으로 봅니다. Pause/체크포인트/타이밍 이벤트의 반복 가능성이 있으면 거부합니다.
- **에디터에서 끈 이벤트**(`"active": false`, 앱 0.6.8, いますぐ輪廻): 게임과 같이 타이밍·체크포인트·자동 타일 계산에서 뺍니다. 지원하지 않는 이벤트도 꺼져 있으면 막지 않습니다. `active`가 없거나 bool이 아니면(`"false"` 문자열, `0`) 켜진 것입니다. RepeatEvents·SetConditionalEvents의 태그 검사는 꺼진 이벤트도 계속 봅니다(더 엄격한 쪽).
- **강제 로드**(콘솔 `load -force`, 앱 0.6.8, 사용자 규칙): Unsupported 곡을 [Y/n] 뒤에 검증된 다른 형식으로 읽습니다(`ParseForced`: 낮은 형식을 가까운 것부터, 없으면 높은 것). 지금 파서에는 형식 번호에 따른 규칙이 없으므로 **형식 검사만 건너뛰는 것**과 같습니다. 성공한 곡은 캐시 `ForcedFormatVersion`과 곡 목록의 `forced`로 구분하며 Supported로 섞지 않습니다.
- 곡/아티스트 메타데이터를 읽고 `<color>` 등의 표시 태그를 제거.
- **MultiPlanet**(0.9.2): 두·세 행성을 지원합니다. 행성 수는 `speed`처럼 뒤 타일로 이어지고, 게임이 회전을 재기 전에 진입각을 `scrMisc.GetInverseAnglePerBeatMultiplanet(n) = π(n−2)/n`(게임의 π는 `3.1415926`)만큼 옮기므로 그 구간의 모든 타일이 그만큼 깁니다(세 행성 = 60°, 두 행성 = 0이라 변화 없음). 방향은 회전을 재는 부호와 같아 상쇄됩니다. 미드스핀 타일은 0, 미드스핀 다음 타일은 한 바퀴(게임의 `6.28318548202515`)를 더 뺍니다. 첫 타일부터 세 행성인 맵과 네 행성 이상은 거절합니다.
- **Hold와 미드스핀**(0.9.2): Hold 다음 타일이 미드스핀이어도 지원하며, 떼는 자리는 **다음 누르는 타일**입니다 — `scrPlayer.Hit`이 미드스핀에서 `midspinInfiniteMargin`을 켜고 `UpdateHoldKeys`가 곧바로 한 번 더 `Hit`을 하므로 잡은 키가 그대로 넘어갑니다. **연속된 Hold**(0.9.2, 2026-10-04): 지원합니다. 타일마다 `holdLength × 2`박이 따로 더해지고(`CalculateSingleFloorAngleLength`는 그 타일만 봄), 사이의 hit은 앞 롱노트를 떼면서 다음을 **누르는** 지점입니다 — `scrPlayer.UpdateHoldKeys`가 `_nextTileIsHold`(= `currFloor.nextfloor.holdLength > -1`)이면 `holdCompletion`을 기다리지 않고 대기 중인 키 입력을 소비합니다. 그 종류가 `HitKind.HoldChain`이고, 롱노트가 끝나는 그 순간의 같은 레인 노트는 허용합니다. 자동 타일에서 끝나는 연속 Hold만 아직 미지원입니다(보여 줄 떼기가 없음).
- **직접 조작 FreeRoam**(0.9.2, 2026-10-04): 자동 구간과 타이밍이 같습니다(코어 패스의 이벤트 44는 auto 플래그를 보지 않음). 구간 동안은 노트를 두지 않고 화면에 "미니게임"만 띄우며, 구간을 시작하는 타일은 평소처럼 눌립니다. 구간 안의 자유 이동은 트랙의 hit이 아니므로 표시하지 않습니다.
- **pitch**(0.9.2, 2026-10-04): 레벨의 `pitch`가 곡 전체의 재생 속도라서 **모든 실제 시각을 그 값으로 나눕니다**. 근거: `scrConductor.SetupConductorWithLevelData`가 `song.pitch = pitch × 0.01`(게임에서는 `GCS.currentSpeedTrial`을 더 곱함)로 두고, `CalculateFloorEntryTimes`가 `entryTime` 옆에 `entryTimePitchAdj = entryTime / song.pitch`를 둡니다. `get_pitch`는 int로 언박싱하므로 정수만, 0 이하는 곡이 멈추므로 거절합니다. 게임 동기화의 타일 대조는 모드가 박 기준 `entryTime`을 보내므로 pitch≠100에서는 맞지 않습니다.
- **쉼표 생략**(0.9.2, 2026-10-04): 게임은 레벨을 `GDMiniJSON`으로 읽고(`LevelData.LoadLevel` → `RDFile.ReadAllText` → `Json.Deserialize`) 그 파서는 어느 메서드에도 `throw`가 없으며 다음 멤버를 switch의 default로 읽으므로 쉼표가 없어도 읽힙니다. `AdofaiJson`이 메모리에서만 쉼표를 넣습니다 — 배열에서는 모든 값 앞에, 객체에서는 이름 앞에만(게임도 숫자를 이름으로 읽지 못함). 문자열 안과 주석은 건드리지 않고, 원본에는 쓰지 않습니다.
- **Toggle 문자열**(0.9.2, 2026-10-04): 에디터는 토글을 `"Enabled"`/`"Disabled"`로 적습니다. `LevelEvent.Decode`가 PropertyType 1에서 `"Enabled"`만 true로 바꿔 넣고 `GetBool`이 그 bool을 읽으므로, AutoPlayTiles의 `enabled`도 같은 규칙으로 읽습니다.
- 알 수 없는 이벤트, 타이밍 이벤트의 비영(非零) angleOffset, 위 조건 밖의 Hold·FreeRoam 등의 미검증 조합은 Unsupported. 잘못된 JSON/필드는 Error.

모든 `.adofai`를 지원한다는 뜻은 아닙니다. 현재 범위 밖 이벤트를 지원하려면 규칙과 테스트 근거를 먼저 추가해야 합니다.

## 계산 근거

공개 구현 [SharpFAI FloorTimingCalculator](https://github.com/adofaiex/SharpFAI/blob/main/SharpFAI/Util/FloorTimingCalculator.cs)의 floor 입·출구 각도와 시간 환산을 확인했습니다. 직선 및 속도 변경의 floor 적용 시점은 [해당 프로젝트 테스트](https://github.com/adofaiex/SharpFAI/blob/main/SharpFAI.Tests/TestGetNoteTimesNew.cs)로 확인했습니다. 중간 회전에서 hit를 추가하지 않는 처리는 [ADOFAI map combiner의 시간 계산](https://github.com/Lipcu-pku/ADOFAI_map_combiner/blob/main/map%20combiner.py)과 대조했습니다. 이 저장소의 쓰기·매크로 기능이나 패키지는 도입하지 않았습니다.

두 행성의 각도 차를 회전 방향에 따라 양의 360도 범위로 정규화하고, 일반 floor에서 회전량이 0 또는 한 바퀴와 **1e-6 rad(약 0.0000573°) 이내**면 360도 회전으로 취급합니다(`scrLevelMaker.CalculateSingleFloorAngleLength`: `|각도| ≤ 1e-6` 또는 `!(|각도| < 6.28318430717959)`이면 2π, 중간 회전이면 0). 앱 0.6.5까지는 도 단위 1e-8 기준이라, 중간 회전 뒤 3.2e-6° 같은 잔여 회전을 0으로 계산했습니다(Tanchiky - ENERGY SYNERGY MATRIX 5곳, 앱 0.6.6 수정). `시간(ms)=회전 각도/180 × 60000/BPM`입니다. floor 이벤트는 해당 floor에서 다음 floor로 이동하는 구간에 적용합니다. 중간 회전은 방향을 180도 뒤집고 추가 이동 시간을 만들지 않습니다. 시작 마커 floor 0 및 중간 회전 floor는 hit 목록에서 제외합니다.

Pause가 있으면 해당 floor에서 다음 floor까지의 간격에 `duration × 60000 / 해당 floor의 BPM`을 더합니다. 이후 hit와 체크포인트 활성화 시각도 함께 이동합니다. countdownTicks를 타격 노트로 생성하지 않습니다. [SharpFAI의 추가 박자 처리](https://github.com/adofaiex/SharpFAI/blob/main/SharpFAI/Util/FloorTimingCalculator.cs)와 설치된 게임 파일의 `scnGame.ApplyCoreEventsToFloors`, `scrLevelMaker.CalculateFloorEntryTimes`에서 이 처리를 확인했습니다.

`pathData` 대응은 설치된 게임의 `ADOFAI.FloorHelper.TryGetPathInDegrees`/`MigratePathData`를 파일에서 ReflectionOnly로 읽어 확인했습니다. 15도 간격의 절대 문자 순서는 `RpJEToUqGQHWLxNZFVDYBCMA`이며 `!`는 999입니다. 문자 하나당 floor 하나를 유지하므로 이벤트 floor 번호가 밀리지 않습니다. 음수 각도는 `scrLevelMaker.InstantiateFloatFloors`의 일반 각도 처리와 modulo 기반 시간 환산을 확인했습니다. 게임 파일 식별 SHA-256은 [체크포인트 근거](checkpoints.md)에 기록된 파일과 같습니다. 외부 게임 코드를 실행하거나 프로젝트에 복사하지 않았습니다.

형식 version 8·PositionTrack·빈 태그 RepeatEvents(2026-09-14, Plum - Megamix 대응)는 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다.
- 레벨 형식 `version`을 읽는 코드는 `ADOFAI.LevelData.LoadLevel`(version 7일 때만 `"enabled"`→`"active"` 치환), `scrLevelMaker.MakeLevel`(에디터에서 version 9일 때만 이벤트 이전), `LevelData.Copy`, 장식 컴포넌트 이전뿐이며 version 8 전용 분기는 없습니다. Megamix의 `SetSpeed`(speedType/beatsPerMinute/bpmMultiplier)·`Twirl` 필드 구성과 pathData 문자는 이미 검증된 version 4 맵과 같습니다.
- `scrLevelMaker.CalculateFloorEntryTimes`는 floor의 entryangle/exitangle/isCCW/midSpin/speed/extraBeats/holdLength/numPlanets만 읽고 위치는 읽지 않습니다. `scnGame.ApplyEventsToFloors`의 PositionTrack 분기는 위치 벡터(startPos/offsetPos)만 계산하며, 이 메서드 전체에서 타이밍 필드에 쓰는 곳은 Hold의 holdLength와 MultiPlanet의 numPlanets뿐입니다.
- 같은 메서드는 이벤트에 `eventTag` 속성이 있을 때만 `Split(" ")`으로 나눠 반복 정의와 맞춥니다. `eventTag` 속성이 없는 SetSpeed/Twirl은 반복 대상이 되지 않습니다.

`SetText`·`ScreenTile`·`ScreenScroll`과 숫자 `angleCorrectionDir`(2026-09-15, 파서 `adofai-v8-screen-events`, NoLongerNull - Search Party 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다.
- 게임 코드 전체에서 `scrFloor`의 타이밍 필드(entryangle/exitangle/isCCW/midSpin/speed/extraBeats/holdLength/numPlanets/entryTime)에 값을 쓰는 메서드는 `scnGame.ApplyEventsToFloors`(내부 `ApplyCoreEventsToFloors`), `scrLevelMaker`의 floor 생성·시간 계산, `scrFloor` 초기화, 프리롬·메뉴·특정 공식 레벨 전용 코드뿐입니다.
- `ApplyCoreEventsToFloors`가 비교하는 `LevelEventType`은 1~3(SetSpeed/Twirl/Checkpoint), 33(Hold), 43(MultiPlanet), 44(FreeRoam), 48~54(Pause 등)입니다. SetText=21, ScreenTile=36, ScreenScroll=37은 없습니다.
- 효과 클래스 `ffxSetTextPlus`는 태그가 맞는 텍스트 장식의 글자만, `ffxScreenTilePlus`/`ffxScreenScrollPlus`는 화면 효과 컴포넌트의 tileX/tileY·scrollSpeed만 바꿉니다.
- `LevelEvent.Decode`는 enum 속성 값이 숫자면 `Enum.ToObject`, 문자열이면 `Enum.Parse`로 읽고, `AngleCorrectionDirection`은 Backward=-1, None=0, Forward=1입니다. 따라서 숫자 -1만 Backward로 허용하고 0·1은 이전처럼 거부합니다.

형식 version 2(2026-09-15, 파서 `adofai-v9-format2`, S3RL - Hentai 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다.
- 레벨의 `version` 필드를 읽는 코드는 `LevelData.LoadLevel`(7일 때만 `"enabled"`→`"active"`), `scrLevelMaker.MakeLevel`(에디터에서 9일 때만), `LevelData.Copy`(복사), `LevelEvent.Decode`의 장식 컴포넌트 이전(12 이하일 때 `scrLockToCamera`의 lockRot·scale만)뿐입니다.
- JSON의 `"version"` 문자열을 쓰는 레벨 코드는 저장(`LevelData.EncodeToDictionary`)과 곡 선택 목록(`LevelDataCLS.Decode`: 19보다 크면 불러오기 결과만 바꿈)뿐입니다. 따라서 version 2에만 적용되는 타이밍 변환은 없습니다.
- 이 맵은 구형 `pathData`, SetSpeed(Bpm/Multiplier)·Twirl과 이미 허용한 시청각 이벤트(MoveCamera, ColorTrack, ShakeScreen, PositionTrack, Flash, SetHitsound, MoveTrack, SetFilter, Bloom, CustomBackground)만 씁니다.

형식 version 19와 `SetParticle`(2026-09-15, 파서 `adofai-v10-format19`, 유괴된돌고래 - 야르송 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다.
- 위 version 2 항목과 같이, 레벨 version을 읽는 분기는 7·9·12 이하(장식)·19 초과(곡 선택 목록)뿐이라 version 19 전용 타이밍 변환은 없습니다.
- version 19 설정 중 `legacyTween`(`scrController.legacyTween`), `legacyFlash`·`legacyCamRelativeTo`(`scnGame.ApplyEventsToFloors`의 연출 처리)는 연출에만 쓰이고, `disableV15Features`는 저장(`LevelData.EncodeToDictionary`)에만 쓰입니다. `legacyPause`·`planetEasePartBehavior`·`scalingRatio`는 `LevelData` 필드가 아닙니다.
- `SetParticle`은 `LevelEventType` 63이며, floor 타이밍 필드를 쓰는 `ApplyCoreEventsToFloors`가 비교하는 번호(1~3, 33, 43, 44, 48~54)에 없습니다. 효과 클래스 `ADOFAI.FloorFX.ffxSetParticlePlus`는 태그가 맞는 파티클 장식의 `ParticleSystem` 설정(방출량·크기·수명·속도·모양)과 재생만 바꿉니다.
- 이 맵의 `RepeatEvents`(version 19의 `repeatType`·`floorCount`·`executeOnCurrentFloor` 사용)는 PlaySound와 MoveTrack만 반복하고, eventTag가 붙은 타이밍 이벤트는 없어 기존 RepeatEvents 규칙으로 허용됩니다.
- 위 Megamix `backup.adofai`(version 19)는 여전히 레벨 옆 에디터 자동 저장이라 곡 목록에 올리지 않습니다.

`SetDefaultText`(2026-09-15, 파서 `adofai-v11-default-text`, 柊マグネタイト - 雑魚 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다(`.local/inspect-enum.ps1`, `inspect-checkpoint.ps1`).
- `LevelEventType` 60입니다. `ApplyCoreEventsToFloors`의 비교(1~3, 33, 43, 44, 48 기준 switch 48~54)에도, `scnGame.ApplyEventsToFloors`의 비교(14 기준 switch 14~16, 43 기준 switch 43~51, 44 기준 switch 44~47, 개별 9~19·26·30·31·33·54)에도 없습니다.
- 효과 클래스 `ffxSetDefaultText`는 HUD 글자(`scrHUDText`)와 곡 제목(`txtLevelName`)의 색·그림자·위치·문구, 클리어/완벽 문구(`customTxtCongrats`·`customTxtPurePerfect`)만 바꿉니다.
- 이 맵의 RepeatEvents는 MoveCamera·MoveDecorations만 반복하고, 체크포인트 4개(floor 53·205·206·268)는 모두 일반 타일에 있습니다.

`Hold`·`AutoPlayTiles`와 나머지 연출 이벤트(2026-09-15, 앱 0.1.1, 파서 `adofai-v12-hold-auto`, AnythingBecomeMoe - ヤラララ 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다(`.local/inspect-field-readers.ps1`, `inspect-event-compares.ps1`, IL 덤프 `.local/il-*.txt`). 구현 기록은 [롱노트·자동 타일](long-notes-plan.md)에 있습니다.
- **Hold 필드:** `ApplyCoreEventsToFloors`(LevelEventType 33)는 다음 타일이 있고 duration ≥ 0이면 `holdLength = duration`, 아니면 -1을 씁니다. `ApplyEventsToFloors`는 `distanceMultiplier/100`을 `holdDistance`, `landingAnimation`을 `showHoldTiming`에 씁니다(표시용).
- **Hold 타이밍:** `scrLevelMaker.CalculateFloorEntryTimes`는 `holdLength > 0`이면 회전 시간 뒤, `extraBeats` 앞에 `holdLength × 2 × local10`을 더합니다. `local10 = GetTimeBetweenAngles(0, π, speed, bpm)`은 한 박 시간이라 duration 1마다 2박입니다. `CalculateSingleFloorAngleLength`도 각도 길이에 `holdLength × 2π`를 더합니다.
- **Hold 입력:** `scrPlayer.UpdateHoldBehavior`는 여유값(`1 - minAngleMargin/(π(2·holdLength+1))`) 이상 진행한 뒤 키를 떼거나(또는 strictHolds가 아닐 때 다시 누르면) `Hit()`으로 다음 타일 hit를 처리합니다. 그 전에 떼면 `requireHolding`일 때 `Die`입니다. 다음 타일도 hold면 떼기로 hit하지 않는 분기가 있어 연속 Hold는 허용하지 않습니다. `holdLength > -1`이면(duration 0 포함) hold 입력으로 처리하고, `CalculateFloorEntryTimes`는 `holdLength > 0`일 때만 시간을 더하므로(IL `ble.s`), duration 0은 **추가 시간 없이 다음 hit에서 떼는 롱노트**입니다(앱 0.6.5, Up In My Jam; `ApplyCoreEventsToFloors`는 다음 타일이 있고 duration ≥ 0이면 그 값을 씀).
- **AutoPlayTiles:** `ApplyEventsToFloors`는 루프 전에 상태를 false로 두고, 이벤트의 `enabled`·`showStatusText`·`safetyTiles`를 바꾼 뒤 각 타일의 `auto`·`showStatusText`·`isSafe`에 복사하므로 다음 이벤트까지 이어집니다. `scrPlayer.get__nextTileIsAuto`가 다음 타일의 `auto`를 읽어 도착 hit를 자동 처리합니다. `isSafe`는 `Die`·`CheckPostHoldFail`·되감기에서만 읽습니다.
- **Hold → 자동 타일(앱 0.6.7):** `scrPlayer.UpdateHoldBehavior`(IL `.local/il-hold-behavior.txt`)는 다음 타일이 자동이면 떼기 판정 블록 전체(`Hit()`과 일찍 뗄 때의 `Die`)를 건너뛰고, 그 타일은 게임이 자동으로 칩니다. 그래서 이 Hold는 누르기만 하는 hit이고 다음 타일은 `Auto`입니다. hold 시간(`holdLength × 2`박)은 그대로 더합니다.
- **FreeRoam(앱 0.6.7):** `.local/inspect-freeroam.ps1`로 필드·메서드 참조를 찾고(`.local/freeroam-refs.txt`, `freeroam-il-refs.txt`), `scnGame.ApplyCoreEventsToFloors`·`scrController.UpdateFreeroam`(`.local/il-update-freeroam.txt`)·`scrPlanet.MoveToNextFloor`(`.local/il-move-next-floor.txt`)의 IL을 읽었습니다. `ApplyCoreEventsToFloors`(LevelEventType 44)는 다음 타일이 있고 `GetInt("duration") ≥ 2`일 때만 `a = floor.angleLength / 3.14159274101257`로 두고 `extraBeats += (float)Math.Max(a, duration − a)`를 더합니다(countdownTicks·angleCorrectionType·효과음도 기록). `angleLength`는 그보다 먼저 `ApplyEventsToFloors`에서 계산돼 회전 반전이 반영되지 않았을 수 있으므로, 방향과 무관한 180°·360° 회전만 허용합니다. `CalculateFloorEntryTimes`는 각도를 직접 다시 계산하고 angleLength·angleCorrectionType을 읽지 않습니다. 구간 끝은 `UpdateFreeroam`이 `다음 타일 entryTime − freeroamEndEarlyBeats`에 입력을 잠그고 행성을 경로로 되돌리는 것이며(`outTime`은 이 되돌림 연출 길이), 다음 타일은 그 entryTime에 평소처럼 칩니다.
- **에디터에서 끈 이벤트(앱 0.6.8):** `.local/inspect-active.ps1`(결과 `.local/active-refs.txt`)로 `ADOFAI.LevelEvent.active`를 읽는 곳을 찾았습니다. 타이밍 쪽은 `scnGame.ApplyEventsToFloors`뿐이고, 나머지는 장식(`UpdateDecorationObjects`)과 에디터입니다. `ApplyEventsToFloors`는 첫 루프에서 이벤트를 floor별 목록에 넣을 때 `active`가 거짓이면 건너뛰며(IL 0x0055 `brfalse`, `.local/il-apply-events.txt`), 이 목록만 `ApplyCoreEventsToFloors`로 넘어갑니다. `LevelEvent.Decode`는 `"active"` 값이 bool일 때만 그 값을 쓰고, 없거나 다른 형이면 참입니다(`.local/il-levelevent-decode.txt`).
- **형식 version 3(앱 0.6.8, ToTheMoon - 정령의 성):** 게임이 레벨 `version`을 읽는 곳(위 형식 2 항목)에 3 전용 분기가 없어 2·4와 같은 경로입니다. 이 맵은 `pathData`, SetSpeed·Twirl과 이미 허용한 연출 이벤트만 씁니다.
- **연출 이벤트:** HallOfMirrors 27, SetHoldSound 34, AddObject 58, SetObject 59, SetFrameRate 61, EmitParticle 64는 `ApplyEventsToFloors`·핵심 floor 함수·`scrLevelMaker` 어디에서도 비교하지 않습니다(AddObject의 Floor 형 오브젝트는 장식이며 레벨 floor 목록을 만들지 않음). Hide 50은 `hideJudgment`·`hideIcon`만 쓰며 이 필드는 `scrPlayer.Die`/`OnDamage`와 `scrFloor.UpdateIconSprite`만 읽습니다. ScaleRadius 52는 `radiusScale`만 쓰고 `CalculateFloorEntryTimes`·`CalculateSingleFloorAngleLength`는 이 값을 읽지 않습니다(행성 위치·프리롬 코드가 읽음).
- 독립 기준 계산기(`GameRuleTimingComparison`)도 hold 시간과 hit 종류(자동·hold 시작·hold 끝)를 파서와 따로 계산해 대조합니다.

형식 version 11과 `AddText`(2026-09-17, 앱 0.1.7, 파서 `adofai-v13-format11-text`, Raimukun - C†hulhu 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다.
- version 11은 12 이하에서만 도는 `ADOFAI.LevelEvent.Decode`의 장식 컴포넌트 이전(`MigrateComponentsObject`)에만 걸립니다. 이 함수는 `scrLockToCamera`의 `lockRot`·`scale`을 `lockRotation`·`lockScale`·`scaleMultiplier`·`lockPos`·`parallax`로 옮길 뿐이라 타이밍과 무관하고, 나머지 version 분기는 7·9와 19 초과(곡 선택 목록)뿐입니다. 따라서 version 11은 이미 허용한 2·8·19와 같은 범주입니다.
- `AddText`는 `LevelEventType` 20이며, `ApplyCoreEventsToFloors`의 비교(1~3, 33, 43, 44, 48~54)에도 `scnGame.ApplyEventsToFloors`의 비교(9~19, 26, 30, 31, 33, 43·44 기준 switch, 54)에도 없습니다. 20을 읽는 곳은 `scrDecorationManager.CreateDecoration`뿐이고, `prefab_textDecoration`을 만들어 `decText`를 `GetStringLocalized`로 표시합니다(AddDecoration 19와 같은 장식 경로).

`SetConditionalEvents`(2026-09-17, 앱 0.1.9, 파서 `adofai-v14-conditional-events`, Plum - Timeline(remake) 대응)도 같은 게임 파일을 ReflectionOnly로 읽어 확인했습니다(`.local/inspect-conditional.ps1`, `inspect-il-refs.ps1`, `inspect-none-tags.ps1`).
- `LevelEventType` 35입니다. 이 값을 비교하는 곳은 `scnGame.ApplyEventsToFloors` 안의 필터 람다(`LevelEvent.info.type`을 읽음)와 `scnGame.PrepVfx`의 람다뿐이며, 타이밍을 만드는 `ApplyCoreEventsToFloors`와 `scrLevelMaker`에는 없습니다(전체 타입 IL에서 35 비교를 검색).
- `ApplyEventsToFloors`는 이 이벤트로 `scrFloor.hasConditionalChange`(bool)만 쓰고, 판정 결과별 효과 목록을 채웁니다. `scnGame.GetFloorConditionalEvents`가 읽는 목록은 `perfectEffects`·`earlyPerfectEffects`·`latePerfectEffects`·`veryEarlyEffects`·`veryLateEffects`·`tooEarlyEffects`·`tooLateEffects`·`lossEffects`·`onCheckpointEffects`이며 모두 연출(FX) 목록입니다.
- `RDUtils.noneTags`는 `NONE`·`无`·`無`·`AUCUN`·`ŽÁDNÝ`·`NICHTS` 여섯 개뿐입니다. 에디터가 한국어로 적은 `없음`은 여기에 없으므로 파서도 실제 태그로 봅니다.
- Timeline(remake)의 조건 태그가 가리키는 이벤트는 MoveDecorations 31개·Flash 6개·MoveTrack 1개로 모두 시청각 이벤트입니다.

degree 단위의 독립 구현이며 게임의 부동소수점 연산을 비트 단위로 복제하는 것은 아닙니다. 직선·곡선·반전·변속·중간 회전의 수치 테스트를 추가했습니다. 설치된 게임 파일에서 확인한 규칙을 별도 기준 계산기로 구성해 사용자 맵 네 곡의 8,113개 hit를 대조했고 최대 차이는 약 0.012ms였습니다. [비교 근거·결과·실제 플레이 확인 절차](timing-comparison.md)를 참고하세요. 실제 게임 실행에서 모든 hit 시각을 측정·대조한 상태는 아닙니다.

원문 offset=1360ms와 countdown은 오디오 동기화 데이터이며, 현재의 J 시작 방식은 첫 hit를 t=0으로 정규화합니다. offset/countdown을 레인 간격에 섞지 않습니다. 오디오 재생은 별도 미구현 기능입니다.

## 제공 자료 점검

- 원본: `.example/main.adofai`.
- 메타데이터: Plum - R, 기본 BPM 180.
- 각도 729개, 999 중간 회전 3개, SetSpeed 88개, Twirl 84개.
- 생성 hit: 726개.
- 파서 기준 첫 hit: 333.333333ms, 마지막 hit: 139000ms.
- 첫 hit로 정규화한 표시 길이: 약 138666.666667ms.
- SHA-256: `E7FB46B01C535DE9918EAF7A2121C0059C6891322F103D225693984DDB8A2D3A`.

테스트에서 원본 해시와 수정 시각이 유지됨을 확인합니다. 테스트 산출물은 `.local/test-artifacts/`에만 생성합니다. 제공된 폴더에는 `main.adofai`만 있으며 맵이 참조하는 `R.ogg`는 없습니다.

원문의 checkpoint 6개를 파싱합니다. floor 358의 tileOffset=3은 floor 361에서 재시작하며 이후 첫 hit인 floor 362부터 준비합니다. 근거와 지원 경계는 [체크포인트 문서](checkpoints.md)에 기록했습니다.

## 사용 방법

`Launch-Demo.cmd` 실행 → 곡 이름 아래 **예제 (Plum - R)** → 체크포인트 선택 → **시작**.

예제 버튼은 `.example/main.adofai`를 읽기 전용으로 직접 파싱해 대기 상태로 준비하며, 곡 목록과 캐시에는 넣지 않습니다(2026-09-14: 이전에는 동기화해 Workshop의 Plum - R과 두 번 보였음. 이전 캐시에 남은 예제 항목은 앱 시작 시 앱 캐시에서 지웁니다). `setting → language`에서 한국어/영어를 선택할 수 있습니다. 게임 없이 키로 시험하려면 ADOFAI 활성 창 제한을 끄세요. 마우스 시작/재설정 버튼은 그대로 사용할 수 있습니다.

## 사용자 Workshop 읽기 전용 검증

2026-09-13 사용자가 지정한 Workshop 루트의 4개 `.adofai`를 읽어 최신 파서와 실제 노트 생성·구간 준비·렌더링을 검증했습니다. 모든 파일의 SHA-256과 수정 시각이 검증 전후 같았습니다. 원문을 테스트 폴더에 복사하지 않았으며 테스트가 실제 앱 라이브러리 캐시를 갱신하지도 않습니다.

| Workshop ID | 곡 | 버전 | hit 수 | 체크포인트 수 | 첫 hit 기준 길이 |
|---|---|---:|---:|---:|---:|
| 2301231290 | Plum - R | 15 | 726 | 6 | 138.667초 |
| 2509019418 | Sheet Music Boss - RUSH E | 4 | 1357 | 0 | 136.251초 |
| 2997617897 | cardboard box - HeHeHe | 13 | 3310 | 0 | 109.728초 |
| 3530462239 | Camellia - We Could Get More Machinegun Psystyle! | 18 | 2720 | 14 | 310.753초 |
| 2411672670 | Plum - Megamix (2026-09-14 추가) | 8 | 4190 | 0 | 594.667초 |
| 3360214923 | NoLongerNull - Search Party (2026-09-15 추가) | 15 | 1237 | 8 | 362.000초 |
| 2382905669 | S3RL - Hentai (2026-09-15 추가) | 2 | 939 | 0 | 178.113초 |
| 3800883358 | 유괴된돌고래 - 야르송 (2026-09-15 추가) | 19 | 190 | 0 | 20.446초 |
| 3749663480 | 柊マグネタイト - 雑魚 (2026-09-15 추가) | 18 | 359 | 4 | 85.333초 |
| 3775535122 | AnythingBecomeMoe - ヤラララ (2026-09-15 추가, 앱 0.1.1) | 19 | 833 (노트 818) | 0 | 147.720초 |
| 2857041443 | Raimukun - C†hulhu (2026-09-17 추가, 앱 0.1.7) | 11 | 1508 | 0 | 137.058초 |
| 2608390714 | Plum - Timeline(remake) (2026-09-17 추가, 앱 0.1.9) | 13 | 909 | 5 | 122.000초 |
| 3787316137 | ぴれんどらー (Pirendora) - Unstable haze (2026-09-17 추가, 앱 0.2.1) | 19 | 2053 | 14 | 194.046초 |

C†hulhu는 형식 version 11과 `AddText` 36개·`HallOfMirrors` 3개를 쓰며, 위 근거로 허용한 뒤 1508 hit가 모두 노트로 준비됩니다(동타 382개 모두 중앙 쌍, 최대 4·6·8키·Dynamic OFF·동타 0ms·배속 50% 준비, 최대 1·2키는 동타로 중단, 구간 시작 24곳 유지). 게임 규칙 대조 최대 0.001073ms입니다.

Timeline(remake)는 `SetConditionalEvents` 6개가 사망(`lossTag`) 등 판정 결과에 연출을 걸어 둔 곡입니다. 위 근거로 허용한 뒤 909 hit가 모두 노트로 준비되고 체크포인트 5개가 유지되며, 게임 규칙 대조 최대 0.000687ms입니다. 동타는 없습니다.

Unstable haze(3787316137)는 floor 1037의 체크포인트가 **중간 회전 타일 위**(angle 999, `tileOffset` +1)여서 Unsupported였습니다. 앱 0.2.1부터 재시작 타일이 일반 타일이면 허용합니다([체크포인트](checkpoints.md)의 근거).

2026-09-17 감사에서 `He_He_He\low effect.adofai`는 Unsupported입니다. 형식 version 6인 데다 `pitch`가 200이며, 검증한 범위는 pitch=100뿐이라 추측해서 허용하지 않습니다(안전 규칙 9). 앱에서도 같은 이유로 곡 목록에 Unsupported로 남습니다.

ヤラララ는 롱노트 1개(floor 949, 16박)와 자동 타일 hit가 있어 hit 833개 중 818개만 노트입니다(자동 hit와 롱노트 끝 hit 제외). 같은 폴더의 `backup.adofai`는 에디터 자동 저장이라 곡 목록에서 제외합니다. 게임 규칙 대조 최대 0.000187ms이며, 기준 계산기가 따로 계산한 hit 종류도 파서와 같습니다.

Search Party의 floor 752~768은 각도 `0, -180 … -2880`의 헤어핀입니다. 게임 IL은 출구 각도 `(90-각도)×0.01745329`를 r4 연산으로 만들고 360으로 줄이지 않으므로, 곱을 single로 반올림하면 일부 헤어핀의 회전 각도가 1e-6을 넘어 거의 0ms가 됩니다. IL만으로는 런타임 정밀도를 정할 수 없어 사용자가 게임에서 확인했고(2026-09-15, 모두 정박 2박), 파서의 degree 계산이 맞음을 확인했습니다. 기준 계산기도 이 확인에 맞춰 곱을 double로 유지합니다. 이 결론은 IL 근거가 아니라 실제 플레이 확인입니다.

Megamix 폴더에는 `main.adofai`(version 8) 옆에 에디터가 새 형식으로 다시 저장한 `backup.adofai`(version 19)가 있습니다. 경로 데이터와 타이밍 이벤트 1961개는 main과 같지만, version 19 전용 설정(`disableV15Features`, `legacyTween`, `planetEasePartBehavior`, `scalingRatio` 등)의 타이밍 영향은 검증하지 않았습니다.

이전에는 이 백업이 Unsupported 곡으로 목록에 따로 올라가 Megamix가 두 번 보였습니다. 2026-09-14 같은 게임 파일(SHA-256 동일)을 ReflectionOnly로 읽어(`.local/inspect-strings.ps1`, `inspect-checkpoint.ps1`) 다음을 확인했습니다.
- `scnEditor.SaveBackup`: 저장하지 않은 편집이 있으면 레벨 폴더에 `backup.adofai`를 씁니다(열린 레벨 이름이 backup이면 `backup2.adofai`).
- `scnCLS.DisplayLevel` → `scnGame.GetWorldPaths`: 커스텀 레벨 화면은 폴더의 `main.adofai`(와 `sub1.adofai`…)를 씁니다.
- `AdoPackageInstaller.FindLevelFile`: `main.adofai` → 이름이 main인 파일 → 이름이 backup이 아니고 `.`으로 시작하지 않는 파일 → 첫 파일 순으로 고릅니다. `scnEditor.FindAdofaiLevelOnDirectory`도 `backup.adofai`와 `.` 파일을 건너뜁니다.

따라서 `MapFileRules`는 같은 폴더에 backup이 아닌 레벨 파일이 있으면 `backup.adofai`/`backup2.adofai`를 곡으로 올리지 않습니다. Map Sync는 이 파일을 읽지도 캐시하지도 않고(전체 스캔 시 이전 캐시 항목도 제거), 동기화 전의 캐시에서도 곡 목록에 숨깁니다. 폴더에 backup만 있으면 그대로 둡니다. 원본 파일은 건드리지 않으며, 외부 맵 감사는 이 백업을 건너뛰되 원본 해시·수정 시각 검사는 적용합니다. Megamix의 독립 기준 계산 대조 최대 차이는 0.000131ms입니다.

RUSH E는 floor 1의 BPM이 15이므로 첫 hit 뒤 다음 hit까지 4초 간격이 있습니다. 기본 표시 시간 3초에서는 준비 화면에 첫 노트만 보이는 것이 정상입니다.

새 파서를 기존 곡에 적용하려면 **맵 선택 → 맵 동기화 → 곡 불러오기**를 다시 실행합니다. 이전 Unsupported 캐시는 자동 변환하지 않습니다.

명시적으로 지정한 맵 루트만 읽는 검증 명령:

```powershell
.\build.ps1 -Configuration Release -Test -ReadOnlyMapRoot 'C:\Program Files (x86)\Steam\steamapps\workshop\content\977950'
```

기본 회귀 테스트는 외부 맵을 읽지 않습니다. 위 옵션을 줄 때만 해당 루트의 실제 파일을 추가 검증합니다. 캡처와 테스트 자체 fixture는 프로젝트 내부 `.local/test-artifacts/`에만 기록됩니다. 환경 대응 추가 후 최신 검증은 60/60 그룹 통과이며, HeHeHe는 동타 기본값의 명시적 중단 및 0ms 복구를 포함합니다. 이는 실제 게임 플레이와 모든 타격 시각을 대조했다는 뜻은 아닙니다.
