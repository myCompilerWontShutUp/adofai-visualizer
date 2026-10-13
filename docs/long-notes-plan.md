# 롱노트(Hold)·자동 타일(AutoPlayTiles)

게임 근거는 [파서 문서](adofai-parser.md)의 Hold·AutoPlayTiles 절, 노트 규칙 요약은 [노트 규칙](note-rules.md)에 있습니다. 기준 곡: AnythingBecomeMoe - ヤラララ(Workshop 3775535122).

## 사용자 결정

- 롱노트 색은 노트 색과 같습니다(단타면 단타 색, 동타에 속하면 동타 색). 시작·끝 노트면 그 색이 몸통까지.
- **겹침 금지**: 롱노트가 끝날 때까지 그 레인에 다른 노트를 두지 않습니다.
- **자동 타일은 노트로 만들지 않고** 토글도 두지 않습니다.

## 구현

- **hit 종류**: `ParsedTimeline.HitKinds`(`Tap`·`HoldStart`·`HoldEnd`·`HoldChain`·`Auto`, 캐시에 저장). hit 시각·번호는 그대로이고 노트는 `Tap`·`HoldStart`·`HoldChain`만 만듭니다. `HoldEnd`는 떼는 지점이며 체크포인트가 그 위에 놓이면 거부합니다.
- **노트**: `VisualizerNote.HoldEndMs`. 동타·Dynamic·사이클은 누르는 노트만으로 계산하고 배속은 끝 시각도 함께 나눕니다. `PlaybackSession.GenerateSongNotes(timeline, settings, out noteHits)`가 노트별 원본 hit 번호를 주어 준비·체크포인트 도달·곡 정보·콘솔이 씁니다.
- **겹침 금지**: 단타는 롱노트가 끝날 때까지(끝 시각 포함) 그 레인을 피해 패턴의 다음 타일로(`NoteGenerator`), 동타 블록은 사용 중인 레인을 후보에서 뺍니다(`SimultaneousNotePlanner`). 롱노트가 끝나는 **그 순간**의 같은 레인 노트(연속 Hold)는 허용합니다. 마지막에 전부 다시 검사하고 피할 수 없으면 `Placement.HoldOverlap`으로 준비를 중단합니다.
- **자동 타일**: 시작 위치 뒤 첫 hit가 자동이면 다음 누르는 노트부터 준비하고, 누를 노트가 없으면 `Playback.NoPressedNotes`로 중단합니다.
- **렌더링**: 머리~꼬리 막대. 누르는 동안 누른 노트 색으로 판정선에서 줄어들고, 누르지 않으면 단타처럼 입력 인정 범위 끝에 사라집니다(판정선 노트 변속이면 머리가 판정선에서 기다림). 가장 긴 롱노트만큼 앞에서부터 화면 노트를 찾습니다.
- **떼기 효과(시각 전용)**: `끝 − 입력 인정 범위` 전에 떼면 그 롱노트를 끝내고 놓침 효과(`NotePressJudge.Release`). 게임의 여유값(`holdMargin`)을 재현한 것이 아닙니다.
- **콘솔 `note`**: 원본 hit 번호(자동·hold 끝 번호는 비어 보임), 롱노트는 `n949 3 hold=5120 400`처럼 길이(ms)를 붙입니다.
- 테스트: `LongNoteTests.cs`(파서 시각·종류·거부, 자동 타일 준비, 겹침·캐시·콘솔, 렌더링·떼기 픽셀과 캡처 `long-note.png`).

미확인: 실제 게임에서의 롱노트 체감, 게임 여유값과 떼기 효과의 차이.
