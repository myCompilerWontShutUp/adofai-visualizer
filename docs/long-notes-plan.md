# 롱노트(Hold)·자동 타일(AutoPlayTiles) 계획

작성: 2026-09-15 (앱 0.0.12 기준). **앱 0.1.1에서 구현했습니다.** 기준 곡은 AnythingBecomeMoe - ヤラララ (Workshop 3775535122, version 19)입니다. 아래 1~4절은 계획 당시 기록이고, 실제 구현은 5절을 따릅니다.

## 1. 기준 곡에서 확인한 사실 (읽기 전용)

- 타일 950개, 중간 회전 117개, pitch 100, planetEase Linear, 모드 없음.
- `Hold` 1개: floor 949, `duration` 8, `distanceMultiplier` 0, `landingAnimation` true. 곡 마지막 타일 바로 앞입니다.
- `AutoPlayTiles` 9개: 켜짐 floor 349·484·673·930, 꺼짐 floor 350·488·674·780·939. 켜진 구간은 1~9타일로 짧습니다(349, 484~487, 673, 930~938).
- 파서가 아직 모르는 나머지 이벤트: `Hide`, `ScaleRadius`, `SetObject`, `AddObject`(가짜 타일 75개), `HallOfMirrors`, `SetFrameRate`, `EmitParticle`, `SetHoldSound`. 각 이벤트는 허용 전에 게임 근거를 따로 확인합니다.

## 2. 게임 파일 근거 (ReflectionOnly, 실행·참조 없음)

| 항목 | 확인 내용 | 상태 |
| --- | --- | --- |
| Hold 필드 | `scnGame.ApplyEventsToFloors`가 `duration`(int)을 `scrFloor.holdLength`에, `distanceMultiplier/100`을 `holdDistance`에, `landingAnimation`을 `showHoldTiming`에 씁니다. | 확인 |
| Hold 타이밍 | `scrLevelMaker.CalculateFloorEntryTimes`: `holdLength > 0`이면 다음 타일 진입 시각에 `holdLength × 2 × 한 박 시간`을 더합니다(Pause의 `extraBeats × 한 박 시간`과 같은 자리). `CalculateSingleFloorAngleLength`도 각도 길이에 `holdLength × 2π`를 더합니다. 즉 duration 1 = 2박, 기준 곡은 16박입니다. | 확인 (공식 적용 순서는 구현 때 기준 계산기로 재확인) |
| Hold 입력 | `scrPlayer.UpdateHoldBehavior`·`UpdateHoldKeys`·`CheckPreHoldFail`이 `holdCompletion`, `scrController.strictHolds`, `requireHolding`을 읽고 조건에 따라 `Die` 또는 `Hit`를 부릅니다. | **미확인**: 끝에서 떼는 시점 판정 여부, 일찍 떼면 실패인지, 끝에 hit가 하나 더 있는지 |
| AutoPlayTiles | `ApplyEventsToFloors`가 `enabled`를 `scrFloor.auto`, `safetyTiles`를 `isSafe`, `showStatusText`를 상태 문구 표시로 씁니다. `scrPlayer.Hit`·`get__nextTileIsAuto`가 `auto`를 읽습니다. | 필드 확인. **미확인**: 이벤트 이후 타일로 상태가 이어지는 범위, `isSafe`의 의미 |
| Hide | `hideJudgment`·`hideIcon`만 씁니다. 읽는 곳은 `scrPlayer.Die`/`OnDamage`와 `scrFloor.UpdateIconSprite`뿐입니다. | 연출로 보임 (허용 전 정리) |
| ScaleRadius | `radiusScale`을 씁니다. `CalculateFloorEntryTimes`는 읽지 않고 행성 이동·프리롬 코드가 읽습니다. | 타이밍 무관으로 보임 (허용 전 확인) |

## 3. 구현 계획 (사용자 승인 후 진행)

### 3.1 파서·캐시 (먼저, 롱노트 표시 없이 검증 가능)
1. 남은 게임 근거를 확인합니다: Hold 입력 규칙, AutoPlayTiles 상태 범위, 나머지 연출 이벤트.
2. `ParsedTimeline`에 hit별 부가 정보를 추가합니다: hold 길이(ms, 없으면 0)와 자동 타일 여부. hit 시각 배열은 그대로 불변입니다.
3. 파서: Hold가 있는 floor의 다음 진입 시각에 `holdLength × 2 × 한 박 시간`을 더합니다. Pause와 같은 floor에 있을 때의 순서는 기준 계산기로 확인합니다.
4. 캐시 스키마에 두 정보를 추가하고 파서 버전을 올립니다. 이전 캐시는 재동기화 안내를 따릅니다.
5. `GameRuleTimingComparison`에 hold 추가 시간을 넣어 ヤラララ를 대조합니다.

### 3.2 노트 준비
- 롱노트는 한 hit(시작)로 사이클·모드·동타 계산에 들어갑니다. 끝 시각은 표시와 입력 효과에만 씁니다. 끝에 별도 hit가 있다고 확인되면 이 항목을 다시 정합니다.
- **겹침 금지(2026-09-15 사용자 결정):** 롱노트의 시작~끝 동안 그 레인에는 다른 노트를 배치하지 않습니다. 레인 배치(`SimultaneousNotePlanner`·패턴 커서)가 롱노트 레인을 "사용 중"으로 보고 다음 노트를 다른 레인으로 보냅니다. 게임에서는 hold가 끝나는 순간이 다음 타일 hit와 거의 같으므로, 끝 시각과 다음 hit 사이 경계(같은 시각을 겹침으로 볼지)를 게임 근거로 확인해 정합니다.
- 피할 레인이 없으면(예: 최대 1키, 롱노트 중 동타) 동타와 같은 원칙으로 이유를 표시하고 준비를 중단합니다. 최대 키 수 자동 확장과 노트 누락은 하지 않습니다.
- 롱노트가 없는 곡의 배치는 지금과 같아야 합니다(회귀 테스트로 고정).
- **자동 타일은 노트로 만들지 않습니다(2026-09-15 사용자 결정, 토글 없음).** 사용자가 누를 필요 없는 hit는 화면에 그리지 않고, 동타 묶음·Dynamic 간격·사이클·패턴 커서·누름/놓침 효과 계산에서도 없는 것으로 봅니다. 구현 방향:
  - 캐시의 원본 hit 시각과 hit 번호는 그대로 두고, hit별 자동 여부만 함께 저장합니다. 제외는 노트 준비(`PlaybackSession.GenerateSongNotes` 앞 단계)에서만 합니다.
  - 콘솔 `note`의 번호는 원본 hit 순서를 유지하므로 자동 hit 번호는 건너뜁니다(예: `n483 …` 다음 `n488 …`).
  - 체크포인트·구간 시작 뒤 첫 hit가 자동이면, 그 뒤 첫 비자동 hit부터 준비하고 그 노트 레인 키로 시작합니다. 곡 정보의 타격 타일 수도 비자동 hit만 셉니다.
  - 곡 전체가 자동이라 노트가 없으면 이유를 표시하고 Start를 막습니다.
  - 자동 구간이 롱노트(Hold)와 겹칠 때(자동 hold)의 처리도 게임 근거로 확인합니다.

### 3.3 렌더링·입력 효과
- `NoteField`에 시작~끝 길이의 막대를 그립니다. 성능 규칙에 따라 Freeze한 단색 브러시를 쓰고, 화면에 보이는 구간만 순회합니다(롱노트는 시작이 화면 밖이어도 끝이 보이면 그려야 하므로 이진 탐색 범위를 넓힙니다).
- `NotePressJudge`: 시작을 누르면 막대를 누름 상태로, 확인된 게임 규칙에 따라 뗌을 시각 피드백으로만 표시합니다. 게임 판정·점수로 표현하지 않고 입력 이력을 저장하지 않습니다.
- 콘솔 `note` 출력에 hold 길이를 어떻게 적을지 정합니다(예: `n949 3 hold 5120`).

### 3.4 검증
- 합성 맵 테스트: Hold 1개(duration 1·8), Pause와 같은 floor, 자동 구간 경계, 체크포인트·구간 시작이 hold 안/뒤에 있을 때.
- `build.ps1 -Configuration Release -Test -ReadOnlyMapRoot … -CompareGameRules`로 ヤラララ 포함 전체 감사.
- 자동 테스트, 독립 기준 계산 대조, 실제 게임 플레이 확인을 구분해 보고합니다. 롱노트 체감은 사용자 플레이 확인이 필요합니다.

## 4. 결정 사항
- **롱노트 색(2026-09-15 사용자 결정):** 노트 색과 같습니다. 새 색 설정을 추가하지 않고, 단타면 단타 색, 동타에 속하면 동타 색을 따릅니다.
- **겹침(2026-09-15 사용자 결정):** 금지. 3.2 참고.
- **자동 타일(2026-09-15 사용자 결정):** 누를 필요 없는 노트는 표시하지 않으며 토글을 두지 않습니다. 3.2 참고.
- 남은 결정은 없습니다. 구현은 사용자 요청 시 3.1(남은 게임 근거 확인)부터 시작합니다.

## 5. 구현 결과 (앱 0.1.1)

확인한 게임 근거는 [파서 문서](adofai-parser.md)의 Hold·AutoPlayTiles 절에 있습니다. 계획과 달라진 점과 확정 규칙:

- **hit 종류:** `ParsedTimeline.HitKinds`(`Tap`·`HoldStart`·`HoldEnd`·`Auto`, 캐시에 저장, 없으면 모두 Tap). hit 시각·번호는 그대로이고 노트는 `Tap`·`HoldStart`만 만듭니다. hold 끝 hit는 떼는 시점이라 별도 노트가 아닙니다(게임이 떼기로 다음 hit를 처리). `HoldStart` 다음은 반드시 `HoldEnd`이며 체크포인트가 `HoldEnd`에 놓이면 거부합니다.
- **파서:** 0 이상의 정수 duration만, 다음 타일이 일반 타일인 중간 타일에서만 허용합니다. duration 0은 추가 시간 없이 다음 hit에서 떼는 롱노트입니다(앱 0.6.5). 연속 Hold(다음이 hold면 떼기로 hit하지 않음), 자동 타일 위의 Hold, 롱노트 도중 체크포인트는 Unsupported입니다. 다음 타일이 자동 타일인 Hold는 게임이 떼기를 보지 않아 누르기만 하는 단타(`Tap`)로 만들고, 다음 hit는 `Auto`입니다(앱 0.6.7, Up In My Jam).
- **노트:** `VisualizerNote.HoldEndMs`. 동타·Dynamic·사이클은 누르는 노트만으로 계산합니다. 배속은 끝 시각도 함께 나눕니다. `PlaybackSession.GenerateSongNotes(timeline, settings, out noteHits)`가 노트별 원본 hit 번호를 주고, 준비·체크포인트 도달·곡 정보 남은 수·콘솔 번호가 이를 씁니다.
- **겹침 금지:** 단타는 롱노트가 끝날 때까지(끝 시각 포함) 그 레인을 쓰지 않도록 패턴의 다음 타일로 넘깁니다(`NoteGenerator`). 동타 블록은 사용 중인 레인을 후보에서 뺍니다(`SimultaneousNotePlanner`). 마지막에 모든 노트를 다시 검사하고, 피할 수 없으면 `Placement.HoldOverlap` 이유로 준비를 중단합니다(최대 1키 등).
- **자동 타일:** 노트를 만들지 않고 토글도 없습니다. 시작 위치 뒤에 누를 노트가 없으면 `Playback.NoPressedNotes`로 중단합니다. 구간 시작 뒤 첫 hit가 자동이면 다음 누르는 노트부터 준비합니다.
- **렌더링:** 머리~꼬리 막대를 노트 색(동타면 동타 색)으로 그리고, 누른 동안은 누른 노트 색으로 판정선에서 줄어듭니다. 누르지 않으면 탭과 같이 입력 인정 범위 끝에 사라지고, 판정선 노트 변속이면 머리가 판정선에서 기다립니다. 롱노트가 있으면 가장 긴 롱노트만큼 앞에서부터 화면 노트를 찾습니다.
- **떼기 효과(시각 전용):** 누른 롱노트를 `끝 − 입력 인정 범위` 전에 떼면 그 롱노트를 끝내고 빨간 놓침 효과를 냅니다(`NotePressJudge.Release`). 게임의 정확한 여유값(`holdMargin`)을 재현한 것이 아니며 판정·점수가 아닙니다.
- **콘솔:** `note` 번호는 원본 hit 번호(자동 hit·hold 끝 번호는 비어 보임), 롱노트는 `n949 3 hold=5120 400`처럼 타일 뒤에 길이(ms)를 붙입니다.
- **테스트:** `tests/Visualizer.Tests/LongNoteTests.cs` 4개 그룹(파서 시각·종류·거부, 자동 타일 준비, 겹침·캐시·콘솔, 렌더링·떼기 픽셀 검사와 캡처 `long-note.png`). 외부 감사는 노트 수를 누르는 hit 수와 비교합니다.
- **미확인:** 실제 게임에서의 롱노트 체감, 게임 여유값과 비주얼라이저 떼기 효과의 차이.
