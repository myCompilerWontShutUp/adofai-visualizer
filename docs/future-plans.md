# 남은 계획

구현 지시는 따로 받습니다. 게임 동기화는 `docs/game-sync-plan.md`, 명령 체계 개편 후보는 `docs/console.md` 13절도 봅니다.

## 1. 게임 링크로 더 할 수 있는 것 (후순위, 사용자 결정)

- 곡 자동 불러오기(게임이 들어간 레벨 경로 → 캐시에서 로드). 지금 `link auto`는 직접 불러온 곡만 씁니다.
- 게임 배속이 100%가 아닐 때의 따라가기.
- 체크포인트 번호·연습 시작 타일 받기.
- 주의: 모드 사용이 리더보드·업적에 주는 영향은 확인하지 않았고, 게임 업데이트마다 필드가 바뀔 수 있습니다(리플렉션이라 기능만 꺼짐).

## 2. 콘솔 명령 정리 후보 (사용자 결정: 기록만)

"이름만으로 기능이 확실하지 않은데 기능마저 비슷한" 명령의 타협안입니다.

| 명령 | 지금 | 타협안 |
| --- | --- | --- |
| `remove` · `delete` | remove: 수정본만 보관(`restore`) · delete: 영구 삭제 | `remove` 하나로, 곡 리스트 창의 "곡 제거"와 같게. `delete`를 없애고 영구 삭제 옵션도 두지 않음 |
| `cmd` · `help` | cmd: 묶음별 한 줄 목록 · help: 전체 설명 | `help`(`[list]`) = 지금의 cmd, `help all`(`*`) = 전체 설명 |
| `copy` · `paste` | paste는 화면에 다시 찍음, `copy -paste` = `paste -copy` | `paste` → `recall`, `copy -paste` 제거(프롬프트 빈칸 `/paste`도 `/recall`) |
| `song` · `info now` | 같음 | `info`의 기본 키워드를 `[now]`로 두고 `song` 제거 |
| `get` · `property` | property의 구역 보기가 get 여러 번과 같음 | `get [<section>[.<setting>]]` 하나로, 받는 값은 `get -choices <setting>` |

그대로 두기를 권한 것: `option -keywords` = `keyword -options`, `id`·`title` ≈ `info -id`·`playlist -id`, 바로가기 `speed`·`autosave`, 대상이 다른 `restore`·`rollback`.

## 3. 가변 인자 후보

지금은 `scannow`·`install`뿐입니다. 쉼표가 값 안에 들어갈 수 없는 곳만 후보이고, 곡 제목은 쉼표가 들어갈 수 있어 `load`·`retitle`은 안 됩니다.

- `hide`·`show`·`remove <id>, …`, `info <id>, …`, `get <setting>, …`, `help`·`option`·`keyword <cmd>, …`
- `playlist`·`id`·`title <string>, …`(합집합), `report done`·`undone`·`erase <title>, …`

## 4. 레인보다 넓은 동타 묶음 (사용자 결정, 추후 진행)

- 사례: Camellia - Hello (BPM) 2026(3689881235). 25ms 안에 최대 28노트라 레인을 8개로 늘려도 놓을 수 없어 어떤 설정으로도 불러오지 못합니다(동타 판정 2ms 이하만 가능).
- 정한 방식: 묶음을 쓸 수 있는 레인 수(화면 + 켜진 여분 레인)만큼 자르고, 나머지는 자기 시각의 다음 동타로(트릴을 레인 수만큼 굴리는 것과 같음). 지금 불러와지는 곡은 바뀌지 않고, "5노트 동타 → 실패 → 여분·넓은 레인 제안" 흐름은 사라집니다.
- 시제품(`SimultaneousNotePlanner.Group`에서 `end - start < 화면 + 여분 레인` 조건 하나)으로 이 곡 24,397노트와 나머지 곡 전부가 불러와지는 것을 확인했습니다. 남은 일: 실패를 기대하던 시험들(`SimultaneousNoteTests`의 실패 두 개·`ExtraLaneTests`·`SixKeyTests`·`AdvancedFeatureTests`의 넓은 레인 제안)을 새 규칙으로 바꾸고, AGENTS·note-rules의 동타 규칙을 고칩니다.
