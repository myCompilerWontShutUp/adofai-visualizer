# 설계 및 확장 지점

의존 방향: `App → Application → Core`, `App → Infrastructure → Application`.
Core는 WPF 및 파일 시스템에 의존하지 않습니다. 외부 패키지는 아직 없습니다.

## 읽기 전용 맵 접근

UI 조립 지점에서 사용자가 입력한 디렉터리로 `ReadOnlyDirectoryMapSource`를 생성합니다. 수동 Map Sync에서 `IReadOnlyMapDirectory`로 파일 목록을 수집하고 `IReadOnlyMapSource`로 원문을 읽습니다. 기본 Workshop 경로는 디렉터리 입력의 기본값이며 앱 시작 시 스캔하지 않습니다. Load Song은 라이브러리 선택 및 캐시 읽기와 연결되며 맵 소스와 직접 연결하지 않습니다.

맵 API는 상대 파일 목록과 원문 문자열만 반환하며 쓰기 가능한 스트림이나 저장 메서드를 제공하지 않습니다. 절대 경로, 루트 밖으로의 상대 경로, 다른 확장자, Windows 대체 데이터 스트림 및 reparse point 경로는 거부합니다. 폴더 열거·속성 확인과 `File.ReadAllTextAsync`만 사용합니다. 스캔 중 접근 불가/링크 경로는 경고로 보고하고, 파일 읽기 오류는 해당 캐시 항목의 Error 상태로 표시합니다.

경로 검사는 실수 방지용이며 검사와 읽기 사이의 파일 시스템 변경을 완전히 차단하는 보안 샌드박스는 아닙니다. 현재 로더는 원문 전체를 메모리에 읽습니다. 대규모 스캔, 크기 제한, 인코딩 정책은 실제 샘플로 검증한 뒤 추가합니다.

## 향후 모듈

| 위치 | 책임 | 현재 상태 |
| --- | --- | --- |
| Core/Maps | 맵 모델 | RawMapDocument, 불변 ParsedTimeline 및 검증된 체크포인트 |
| Application/Maps | 읽기·파싱·동기화 | Read-only 계약, MapSyncService, 지원 상태를 반환하는 파서 계약 |
| Infrastructure/Maps | 파일 시스템 어댑터 | 읽기만 구현 |
| Infrastructure/Parsing | .adofai 구문 파서 | Plum - R 기반 angleData/두 행성 제한 지원, 미지원 이벤트 명시 |
| Core/Timing | 검증된 규칙 기반 타임라인 | 미구현 |
| Core/Settings | 불변 설정 스냅샷과 검증 | VisualizerSettings |
| Core/Visualization | 동적 타일 수, 사이클 커서, 노트 생성, 위치 공식 | NoteLayout, DisplayOffsets, 동타 배치, NoteFieldGeometry(판정선·제거선) |
| Application/Playback | 재생 상태 및 단조 시계 계약 | OFF/WAITING/PLAYING/PAUSED, 체크포인트 기준 Reset, 자동 체크포인트 토글, 시각 전용 `NotePressJudge` |
| Application/Cache | 사용자 설정과 독립인 캐시 계약 | 읽기 계약과 쓰기 계약 분리 |
| Infrastructure/Cache | 프로그램 저장소 | 프로젝트 내부 JSON 캐시 및 경로 보호 |
| Application/Input | 입력 정책 | 전경 필터, 키 반복 억제 |
| Infrastructure/Input | Windows 입력 | Raw Input 관찰, 전경 프로세스 확인 |
| App/Rendering | Falling Note 렌더러 | 절대 ms 기반 NoteField, 판정선·하단 제거선, 레인 그라데이션, 재생·효과 중에만 다시 그림 |
| App/Themes | 공용 스타일과 테마 | Theme.xaml 스타일(색은 DynamicResource, 모든 버튼의 눌림·켜짐 모양 통일), `Palettes/*.json` 테마 파일(universe·dark·white, 파일 이름 = 테마 id)을 읽는 ThemeCatalog, 설정 창의 정적 별 배경 SpaceBackdrop. 테마 파일을 추가하고 빌드하면 테마가 늘어남. 창마다 병합하므로 Application 인스턴스 없이도 로드 |
| Core/Localization | 언어 파일 | `Languages/*.json`(korean·english, 파일 이름 = 언어 id)을 어셈블리에 포함하고 `Languages.Get/Format`으로 읽음. 없는 키는 영어, 그다음 키 이름으로 대체. App의 `UiText`는 바인딩용 얇은 래퍼. 언어 파일을 추가하고 빌드하면 언어가 늘어남 |
| App/ViewModels, App/Views | 기능별 partial 파일 | `MainViewModel`(명령·상태) · `.Songs`(곡 목록·◀ ▶) · `.Placement`(여분 레인 배치/8키 제안) · `.Overlay`(멈춘 동안 곡 정보). `SettingsPanel`(구성, 메인 창 안에서 열리는 UserControl) · `.Rows`(행·★·도움말) · `.Form`(입력·비교·미리보기) · `.Json`(고급 설정·JSON) |
| App/ViewModels | 화면 상태 및 사용 사례 연결 | MainViewModel, Stopwatch 어댑터 |
| App/Views | 메인 창 및 설정 UI | Map Sync/Load Song, Settings, ON/OFF, Reset/Start, Checkpoint |

미구현 위치는 설계상 예약이며 필요할 때 실제 소스 폴더를 추가합니다. 아직 알려지지 않은 타이밍 공식이나 레인 매핑 기본값을 만들어 넣지 않습니다. 파서는 구문, 타이밍 엔진은 시간 계산, 4키 변환은 표시 정책을 담당하도록 분리합니다.

노트 생성 규칙과 기본 레인 패턴은 사용자 명세로 확정된 값만 구현했습니다. 현재 실제 `.adofai` 파서는 [제한된 이벤트 조합](adofai-parser.md)을 지원하며, 범위 밖은 Unsupported를 반환합니다. `NoteGenerator`는 파서의 hit time(ms) 배열만 받습니다. `RulePreviewTimeline`은 명시적인 합성 자료로 실제 맵 로드/캐시와 분리되어 있습니다. 상세 규칙은 [note-rules.md](note-rules.md)를 참고하세요.

`PlaybackSession`은 WPF를 모르며 `IMonotonicClock`으로 시간을 읽습니다. Reset에서 불변 노트 목록을 생성하고 Start는 시계 기준점과 상태만 바꿉니다. 렌더러는 프레임마다 절대 시각을 조회하여 표시할 뿐 시계를 누적하거나 레인을 결정하지 않습니다. UI의 이벤트 핸들러는 파일 선택/설정 대화상자/키 입력을 ViewModel로 전달합니다.

## 프로그램 저장소

개발 도구 데이터는 프로젝트 내부 `.local/`에 저장하고 빌드 산출물은 각 프로젝트 `bin/`, `obj/`에 저장합니다. 수동 Map Sync만 `.local/app-data/map-cache/library.json`에 기록합니다. 설정·로그·DB는 아직 기록하지 않습니다. 생성자는 디렉터리를 만들지 않고, 캐시가 없을 때 읽기는 빈 목록을 반환합니다.

현재 저장소 루트는 실행 파일의 상위 경로에서 `AGENTS.md`와 `Visualizer.sln`이 있는 프로젝트를 찾아 고정합니다. 찾지 못하면 실패하며 외부 사용자 폴더로 대체하지 않습니다. 향후 배포용 저장소는 별도 작업 범위에서 정해야 합니다.

저장소 모듈은 맵 로더에 주입하지 않습니다. MapSyncService가 읽기 소스와 캐시 writer를 조율합니다. 저장소와 맵 루트의 동일/상하위 관계 및 모든 상위 junction/symlink를 거부하고 읽기·쓰기 전에 재검사합니다. 맵 옆에 sidecar 설정이나 변환 결과를 쓰지 않습니다. 경로 검사와 실제 작업 사이의 악의적인 파일 시스템 변경까지 막는 OS 샌드박스는 아닙니다.

## 캐시와 레이아웃 분리

```text
Read-only .adofai → 수동 Map Sync → ParsedTimeline cache
                                      ↓ Load Song
                              PlaybackSession 보유 타임라인
                                      ↓ User Settings
                              DynamicTimingSelector
                                      ↓ TilePatternCursor / NoteGenerator
                              Renderable Notes → NoteLayout → WPF
```

캐시 스키마 v1은 맵 식별자, 루트/상대 경로, 곡 이름, 원문 hash, 파서 버전, 지원 상태/경고, 원본 hit time, 검증된 checkpoint만 저장합니다. 레인 할당, 패턴, Initial Tile, threshold, offset은 저장하지 않습니다. 지원하지 않는 맵은 Unsupported와 null timeline을 기록합니다. 지원하지 않는 스키마/손상된 캐시는 오류를 표시하고 자동 덮어쓰기하지 않습니다.

스캔은 수동으로만 수행하며 전체 스캔이 성공한 루트는 사라진 항목도 갱신합니다. 일부 폴더를 읽지 못하면 그 루트의 기존 항목을 보존하고 경고합니다. 파일별 실패는 Error로 갱신합니다. 임시 파일을 전용 캐시 안에 생성한 뒤 `library.json`을 원자적으로 교체합니다. 쓰기 실패/취소 시 내부 `.tmp` 파일이 남을 수 있습니다. 다중 앱 인스턴스의 동시 Sync 충돌 조정은 아직 구현하지 않았습니다.

Load Song과 설정 편집은 이미 읽은 캐시 스냅샷을 사용합니다. 규칙 설정 변경 시 현재 체크포인트 기준으로 노트를 재생성합니다. Travel Time/Thickness/Display Offset/Control만 바뀌면 노트 목록과 진행 중 시계를 유지합니다. 향후 맵별 offset은 `DisplayOffsets.MapMs` 및 재생 세션의 별도 값으로 제공하며 원본 timeline에 섞지 않습니다.
