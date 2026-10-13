# 설계와 확장 지점

의존 방향: `App → Application → Core`, `App → Infrastructure → Application`. Core는 WPF·파일 시스템에 의존하지 않고, 외부 패키지는 없습니다. 클래스 지도는 `CLAUDE.md` 2절.

## 읽기 전용 맵 접근

`ReadOnlyDirectoryMapSource`가 `IReadOnlyMapDirectory`(파일 목록)·`IReadOnlyMapSource`(원문)를 구현하며, 상대 파일 목록과 원문 문자열만 돌려주고 쓰기 가능한 스트림·저장 메서드가 없습니다. 절대 경로, 루트 밖 상대 경로, 다른 확장자, 대체 데이터 스트림, reparse point는 거부합니다. 스캔 중 접근 불가·링크는 경고, 파일 읽기 오류는 그 항목의 Error입니다. 경로 검사는 실수 방지용이며 검사와 읽기 사이의 변경까지 막는 샌드박스는 아닙니다.

동기화는 스팀 폴더(읽기 전용)를 앱 저장소의 `maps/custom`으로 `.adofai`만 복사한 뒤 `maps/{base,custom,edited}`를 파싱합니다([맵 저장 구조](map-storage-plan.md)).

## 앱 저장소

- 위치: `%LOCALAPPDATA%\AdofaiVisualizer\`(이 PC의 모든 복사본이 공유) 또는 고급 설정의 데이터 폴더(`data-root.txt`로 기억). 옛 `<저장소>/.local/app-data`는 처음 켤 때 한 번 복사합니다(`StoreMove`, 곡 id를 새 경로로 다시 씀).
- 저장소와 맵 루트의 동일·상하위 관계와 상위 junction/symlink를 거부하고, 쓰기 전마다 재검사합니다. 맵 옆에 sidecar를 쓰지 않습니다.
- 쓰기는 임시 파일 → 원자 교체(`library.json` 등). 생성자는 디렉터리를 만들지 않고, 캐시가 없으면 빈 목록입니다.
- 저장소 루트(업데이트·테스트용)는 실행 파일 위로 올라가며 `AGENTS.md`와 `Visualizer.sln`이 함께 있는 폴더입니다(`WorkspaceMapCache.FindRepositoryRoot`).

## 캐시와 레이아웃 분리

```text
Read-only .adofai → 수동 Map Sync → ParsedTimeline 캐시 (hit 시각·타일·종류·체크포인트·구간·곡 정보)
                                      ↓ Load Song
                              PlaybackSession 보유 타임라인
                                      ↓ 설정 스냅샷
                              DynamicTimingSelector → TilePatternCursor / NoteGenerator → SimultaneousNotePlanner
                                      ↓ (수정본이면 NoteEditLayout.Apply)
                              VisualizerNote → NoteLayout → NoteField (WPF)
```

- 캐시에는 레인·패턴·Initial Tile·경계값·offset을 넣지 않습니다. Unsupported는 null 타임라인과 이유를 기록합니다. 손상된 캐시는 오류를 표시하고 자동으로 덮어쓰지 않습니다.
- 전체 스캔이 성공한 루트만 사라진 항목을 갱신하고, 일부를 읽지 못하면 그 루트의 기존 항목을 보존합니다.
- Load Song과 설정 편집은 캐시 스냅샷만 씁니다. 규칙 설정이 바뀌면 노트를 다시 만들고, 시각·조작 설정만 바뀌면 노트와 시계를 유지합니다.
- `PlaybackSession`은 WPF를 모르고 `IMonotonicClock`으로 시간을 읽습니다. 렌더러는 프레임마다 절대 시각을 조회할 뿐 시계를 누적하거나 레인을 정하지 않습니다.
- `RulePreviewTimeline`은 합성 자료로 실제 맵 캐시와 분리되어 있습니다.

## 확장 지점

- 맵별 표시 보정: `DisplayOffsets.MapMs`·`SetMapDisplayOffset`(UI·저장 없음).
- 테마·언어: `Themes/Palettes/*.json`·`Localization/Languages/*.json`에 파일을 더하고 빌드하면 늘어납니다(파일 이름 = id, 없는 언어 키는 영어 → 키 이름으로 대체).
- 플러그인: `plugins/<이름>/`에 빌드된 채로 두면 앱 빌드가 실행 파일 옆으로 복사합니다.
- 알려지지 않은 타이밍 공식이나 레인 매핑 기본값은 만들어 넣지 않습니다(AGENTS 안전 규칙 7).
