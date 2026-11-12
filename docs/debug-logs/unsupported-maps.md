# 디버그 기록 1 — Unsupported 곡

## 증상

- 곡 목록 제목 오른쪽에 빨간 `Unsupported`(또는 `Error`). 불러오면 준비되지 않고 이유가 나옵니다(콘솔은 `The map is Unsupported.`).
- 파서는 추측하지 않고 첫 이유 하나로 멈추므로, 하나를 고치면 **다음 이유가 드러날 수 있습니다**. 고친 뒤에는 사본으로 곡 전체를 다시 파싱합니다.

## 진단 순서

1. 캐시에서 이유(`Warnings[0]`)를 읽습니다([README](README.md)).
2. **`ParserVersion`을 `AdofaiTimelineParser.CurrentVersion`과 먼저 비교합니다.** 다르면 코드보다 빌드·동기화를 의심합니다 — 사용자가 옛 Release를 실행 중이라 Release가 빌드되지 않았거나, 새 빌드로 Map Sync를 하지 않았습니다.
3. 이유를 분류합니다.

   | 이유 문구 | 할 일 |
   | --- | --- |
   | `지원하지 않는 이벤트: X (floor n)` | 4번 이벤트 조사 |
   | `맵 형식 version N은 미검증입니다` | 5번 형식 조사 |
   | Hold·Pause·FreeRoam·Checkpoint·pitch 등의 조건 문구 | 그 조건의 게임 근거로 범위를 넓힐 수 있는지 |
   | `Error`(JSON·필드 검증 실패) | 파일 손상 또는 게임이 관대하게 읽는 문법(쉼표 생략처럼). 게임 파서 동작을 먼저 확인 |

4. **이벤트 조사**: 사본에서 그 이벤트 JSON을 그대로 보고, 게임 DLL에서 `LevelEventType` 번호가 `ApplyEventsToFloors`/`ApplyCoreEventsToFloors`에서 비교되는지, 타이밍 필드(`entryangle`·`exitangle`·`isCCW`·`midSpin`·`speed`·`extraBeats`·`holdLength`·`numPlanets`)에 쓰는지 확인합니다. 쓰지 않으면 `VisualEvents`에 근거 주석과 함께 넣고, 쓰면 IL로 공식을 확정해 확인된 좁은 조건만 지원합니다.
5. **형식 조사**: 게임이 레벨 `version`을 읽는 곳은 `LoadLevel`(7), `MakeLevel`(9, 에디터), 장식 이전(12 이하), 곡 목록(19 초과)뿐입니다. 그 형식의 맵이 검증한 이벤트·필드만 쓰는지 확인하고 `FormatVersions`에 넣습니다.
6. 고친 뒤는 [README](README.md)의 "고친 뒤".

## `load -force` (마지막 수단)

Unsupported 곡에만, 항상 [Y/n]. 곡 형식보다 낮은 검증 형식을 가까운 것부터, 없으면 높은 것을 시도합니다. 성공하면 캐시 `ForcedFormatVersion`과 목록의 주황 `forced`가 남고(같은 파일인 동안 Map Sync 뒤에도 유지), 실패하면 캐시를 건드리지 않습니다. 지금은 형식별 규칙이 없어 **형식 검사만 건너뛰는 것**과 같으므로 이벤트·조건 때문에 막힌 곡은 풀리지 않습니다. 권하기 전에 정식 지원을 먼저 시도합니다.

## 해결 기록 (날짜순)

- **2026-09-22 · Up In My Jam (3012378858)** — Hold duration 0: `CalculateFloorEntryTimes`는 `holdLength > 0`일 때만 시간을 더하고 `scrPlayer`는 `> -1`이면 hold로 처리 → 추가 시간 없는 롱노트. 이어서 FreeRoam(자동 타일 구간)을 지원. 세 번째 제보는 사용자가 옛 Release를 실행 중이던 것(코드 문제 아님 — `ParserVersion`부터 볼 것).
- **2026-09-23 · ToTheMoon - 정령의 성 (2494144385)** — 형식 3: 형식 3 전용 분기가 없고 맵이 검증된 이벤트만 씀 → `FormatVersions`에 추가.
- **2026-09-23 · Plum - Wake Up, Piano! (2128998212)** — 형식 6: 같은 근거로 추가. 회귀 자료 `tests/Visualizer.Tests/Fixtures/WakeUpPiano.json`(원본 바이트 그대로, SHA-256 `9439F508…C9D8`).
- **2026-10-04 · THE END OF HATSUNE MIKU (3011005213)** — MultiPlanet(진입각을 `π(n−2)/n`만큼 옮김), 미드스핀 위의 Hold(다음 누르는 타일에서 뗌), 연속 Hold(`HitKind.HoldChain`). 근거는 `docs/adofai-parser.md`.
- **2026-10-04 · 불러오지 못하던 곡 전부 (2850594034 · He_He_He low effect · 3011005213)** — 한 곡에서 벽이 일곱 번: 쉼표 생략(GDMiniJSON이 관대함) → 형식 9(에디터 마이그레이션뿐) → Toggle 문자열(`"Enabled"`) → ScaleMargin(시청각) → 직접 조작 FreeRoam(타이밍 동일) → 연속 Hold → pitch(모든 시각을 pitch로 나눔). 파서 `adofai-v23-pitch-chained-holds`. 사본으로 `-ReadOnlyMapRoot` 감사 통과, 게임 자체와의 대조(`link tiles`)는 아직.
- **2026-10-12 · 柿崎ユウタ - 月が綺麗ねと言われたい！ (3770845493)** — Pause `angleCorrectionDir: "Forward"`(floor 75, duration 1.5). 게임에서 방향은 `scrFloor.angleCorrectionType`에만 남고 `CalculateFloorEntryTimes`는 읽지 않으며, `scrPlanet.HandlePause`는 다음 누름 시각·각도를 방향과 무관하게 정하고 보이는 각도 트윈에만 곱함 → Pause는 Backward·None·Forward 모두 같은 시각으로 지원(FreeRoam은 그대로 Backward만). 파서 `adofai-v24-pause-correction`. 사본 재파싱: 604 hit, 기준 계산기와 최대 0.0013ms, 배치 변형 모두 통과. 게임 자체와의 대조(`link tiles`)는 아직.
- **2026-10-12 · Plum - PLUM MEGAMIX 2 (2924697157)** — `Error`: 설정의 `"requiredMods": [],,`(쉼표 두 개). GDMiniJSON은 COMMA 토큰을 읽고 루프를 계속하므로 게임은 읽음 → 값 없이 놓인 쉼표를 메모리에서 뺌(`AdofaiJson`). 게임이 링커로 보낸 타일 시각 6545개와 대조해 오프셋 퍼짐 0.0ms(게임 값 대조). 앱 목록에는 맵 동기화 뒤 나타남.
- **2026-10-21 · ああああ - プロローグ (2798471112)** — 형식 10: 게임이 레벨 `version`을 읽는 곳(7·에디터 9·장식 12 이하·목록 19 초과) 중 10만의 분기가 없어 11과 같은 길이고, 맵이 검증된 이벤트만 씀 → `FormatVersions`에 추가, 파서 `adofai-v26-format-10`. 사본 대조 최대 0.0008ms(232노트, 체크포인트 3), 원본 SHA-256·수정 시각 그대로.
- **2026-10-21 · Camellia - Hello (BPM) 2026 (3689881235)** — Supported지만 배치 실패(노트 954, 13030ms: 4키 구간에 타일 7개 필요). 25ms 안에 최대 28노트(24,397노트·180초)라 레인을 8개로 늘려도 놓을 수 없음. 사용자 결정으로 묶음을 쓸 수 있는 레인(화면 + 여분) 수에서 자르고 나머지는 다음 묶음(`SimultaneousNotePlanner.Group`). 사용자 설정으로 모든 곡을 사본에서 불러 봐서 이 곡만 바뀌는 것을 확인.
- **2026-10-25 · S3RL - play it loud (3790890137)** — "첫 방향이 0도인 두 행성 맵부터 지원": 첫 방향 345°. `InstantiateFloatFloors`가 floor 0 진입각을 3π/2(0° 방향 뒤와 같음)로 두므로 첫 회전도 다른 타일과 같은 식 → 제한과 floor 0의 180° 고정을 없앰, 파서 `adofai-v27-any-first-direction`. 사본 대조 최대 0.0004ms(1387노트, 체크포인트 2), 사용자 설정으로도 준비됨, 원본 SHA-256·수정 시각 그대로.
- **2026-11-12 · 句るり - 忘れもの注意 (3831421210)** — `지원하지 않는 이벤트: SetInputEvent (floor 1)`: 카운트다운에 위·아래 키로 가사(`cn_ly`·`en_ly` 태그의 SetText·MoveDecorations)를 고르고 floor 49에서 `NONE`으로 비움. `ffxSetInputEventPlus`는 키 슬롯에 태그 효과를 걸 뿐이고 `scrPlayer.Hit`은 `ignoreInput`이 켜진 눌림 슬롯에서만 누름을 버림 → `ignoreInput`이 꺼지고 대상이 시청각인 것만 지원, 파서 `adofai-v28-input-events`. 게임이 2026-11-04에 업데이트되어(`Assembly-CSharp.dll` 해시 변경) 계산기가 따르는 IL을 다시 대조: 타이밍 함수는 같음. 사본 대조 최대 0.000243ms(936노트, 체크포인트 7), 사용자 설정으로 4·8레인 준비됨, 원본 SHA-256·수정 시각 그대로.
