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

## 4. 요청받았고 사용자 지시를 기다리는 것 (2026-10-25)

- 8키에서 4연타가 `d f j k`(3·4·5·6)가 아니라 한 손 `a s d f`·`j k l ;`(1~4·5~8)에 오게 — 실험적 기능 설정으로. 정할 것: 4연타의 기준(간격·개수), 연타마다 손을 번갈아 쓰는지, 연타 안의 방향.
- 동타 설정 "4키 초과는 동타만": 5키 이상은 간격(ms)으로 고르지 않고, 4레인으로 모자라는 동타가 왔을 때만 그만큼 레인을 늘림(2·3동타는 해당 없음). 정할 것: 늘린 레인이 그 동타에만인지 그 구간까지인지, 기본값·위치.
- 빠른 단타가 `4 3 2 1` 대신 `4 2 3 1`처럼 엇갈림, RUSH E류의 너무 빠른 `2 3 2 3` — 하드코딩이 아니라 설정 구조로 막기. 조사 결과는 `docs/handoff-history.md` (25).
