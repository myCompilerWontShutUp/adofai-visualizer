# 명령어 콘솔 레퍼런스

Ctrl+T로 여는 콘솔의 **명령·옵션·키워드·설정 이름 전부**를 지금 동작 그대로 적은 문서입니다. 명령 체계를 바꿀 때의 기준 문서이며, 이력은 적지 않습니다(이력은 `docs/handoff-history.md`).

- **원본 코드**: `src/Visualizer.App/Commands/` — `CmdCatalog.cs`(명령 표·옵션·키워드·파서), `CmdSession.cs`(동작·질문·Tab 후보), `SettingPaths.cs`(설정 이름). 이 문서의 표는 그 코드에서 뽑았습니다. 코드와 다르면 코드가 맞고, 이 문서를 고칩니다.
- **콘솔은 앱 언어를 따릅니다**(설정의 언어, 사용자 요청). 문구는 언어 파일(`Languages/*.json`)의 `console` 묶음에 있고 `ConsoleText.T(키)`로 읽습니다. 언어를 추가하면 파일 하나에 창 문구와 콘솔 문구가 함께 들어갑니다. 명령·옵션·키워드 이름, `<id>` 같은 인자 이름, 설정 경로(`timing.speed`), 설정 값(`true`·`none`), 곡 종류(`base`·`custom`·`edited`), 메모 상태(`open`·`done`·`asked`), `[Y/n]`은 입력하는 그대로 둡니다. 곡 제목·경로·메모 본문은 데이터라 그대로 나옵니다. `update.ps1` 자체가 찍는 문장(네트워크 실패 등)은 스크립트의 말이라 영어로 나옵니다.
- **콘솔에 없는 것**: 지우기(언인스톨)는 콘솔에서 하지 않습니다. `uninstall`이나 `help uninstall`을 치면 "Uninstalling is not done from inside the visualizer: close it and run .\uninstall.ps1 in PowerShell, in its folder."만 답합니다(사용자 규칙). 저장소에 올리는 명령도 없습니다(편집기의 git으로 올림).

---

## 1. 입력 규칙

- **형식**: `명령 [옵션…] [키워드·인자…] [옵션…]`. 옵션은 키워드·인자의 **앞이든 뒤든** 올 수 있습니다. 다만 **자유 글자**(`set`의 값, `import`·`override`의 JSON, `load`·`retitle`의 제목, `prompt`의 제목) 뒤에 오는 `-…`는 글자의 일부로 읽으므로 옵션은 그 앞에 둡니다. `report`는 제목이 자유 글자지만 낱말 앞뒤의 옵션을 스스로 걸러 냅니다(`report write -copy 제목`).
- **옵션**은 띄어쓰기로만 구분하고 순서는 상관없습니다. 도움말은 `[-id | -title] [-hidden | -shown] [-copy]`처럼 적고, **같은 `[ ]` 묶음의 옵션은 함께 쓸 수 없습니다.** 같은 옵션을 두 번 써도 오류입니다.
- **값이 붙는 옵션**: `-slot:3`, `-speed:keep`, `-version:1.0.0`(`=`도 같음, `-sl3`·`-v1.0.0`처럼 붙여 써도 읽음). 이름만 치면 기본값입니다(`-speed` = `keep`).
- **짧은 별칭**(`-c`, `-p`, `-og` …)은 계속 동작하지만, 도움말·Tab·오류 문구는 **긴 이름만** 씁니다.
- **가변 인자**(`scannow`, `install`): 쉼표로 나누고 띄어쓰기는 자유입니다(`scannow maps, linker` = `scannow maps,linker`). 가변 인자 뒤에는 다른 인자가 올 수 없어 사용법 맨 끝에 `<area>, …`로 적습니다. 빈 자리(`maps,`)와 쉼표 없는 두 낱말(`maps linker`)은 오류이고, `all`·`*`는 혼자 씁니다.
- **대소문자**는 구분하지 않습니다(곡 id 포함). 값 자리의 `-1`(마지막), `-12`처럼 하이픈 뒤가 글자가 아니면 옵션으로 보지 않습니다.
- **인자 이름**: `<id>`는 8자 곡 id 또는 `now`(불러온 곡), `<cmd>`는 명령 이름, `<string>`은 곡 묶음 키워드, `<tile>`은 타일 번호(1 이상의 정수), `<range>`는 노트 범위입니다(4절 `note`).
- **Tab 자동완성**: 치고 있는 단어를 완성하고, 다시 누르면 다음 후보, Shift+Tab은 이전 후보입니다. 후보는 **그 명령이 실제로 다루는 것**입니다 — 모든 명령은 첫 인자에 자기 키워드를, `-`를 치면 자리가 어디든(자유 글자 뒤 제외) 아직 쓰지 않은 옵션을 내놓습니다. 그 밖에: `load`는 제목의 어느 부분을 쳐도 원곡 제목 전체(`load hu` → `load Raimukun - C†hulhu`), `report`는 낱말과 메모 제목, `prompt`는 저장된 프롬프트, `set`·`get`·`property`는 `.` 전까지 구역·그 뒤 설정·`set` 값 자리에서는 고를 수 있는 값, **`go`와 `unpin`은 그 곡의 핀 타일**, `id`를 받는 자리는 곡 id입니다. 빈 줄에서는 `cmd`부터 나옵니다.
- **기록과 질문**: ↑↓ 이전 명령, Esc 줄 비우기. `[Y/n]`은 질문 줄 끝에서 답하고(빈 답은 다시 물음), `-proceed`로 건너뛴 질문은 `[Y/n] Y (-proceed)`로 남습니다. 번호 목록은 `Pick 1-n:` 뒤에 번호로 답하며(`01` = `1`), 잘못 친 답은 한 번만 다시 묻고 두 번째에는 `Cancelled.`로 끝냅니다.

## 2. 옵션과 키워드의 원칙 (사용자 규칙 — 새 명령은 이 절을 지켜야 합니다)

**옵션 `-name`은 부사입니다.** 명령이 하는 일은 그대로 두고 결과의 표현·범위·절차만 바꿉니다. 전역 표에 올라가 **어느 명령에 붙어도 뜻이 같고**, 빼도 명령이 성립합니다.
**키워드는 목적어·동사입니다.** 인자 자리에 오는 정해진 낱말로, 그 명령의 **대상**이나 **동작**을 고릅니다. 명령마다 뜻이 달라도 됩니다.

가르는 질문 넷: ① 그 말이 없으면 무엇을 할지 정해지지 않는가 → 키워드(`link start`, `install linker`) ② 명령이 다루는 대상을 고르는가 → 키워드(`load origin`, `checkpoint last`) ③ 다른 명령에 붙여도 같은 뜻인가 → 옵션(`-copy`, `-proceed`) ④ 출력 모양·범위·질문 생략만 바꾸는가 → 옵션(`-title`, `-hidden`, `-json`).

**옵션의 규칙**

1. **이름 하나에 뜻 하나**(전역). `-force`도 뜻은 하나입니다: 거절될 일을 묻고 나서 그대로 합니다(`load`는 지원하지 않는 곡, `report create`는 이미 있는 제목, `update`는 자기 수정이 있는 복사본·최신이 아닌 버전).
2. **`-force`는 언제나 [Y/n]을 묻습니다.** `-proceed`가 대신 답할 수 없어 함께 쓰면 오류이고(`-force always asks first, so -proceed cannot answer for it.`), 둘을 다 가진 명령은 한 묶음 `[-force | -proceed]`에 둡니다.
3. **이름은 낱말**(`-origin`, `-speed`). 두 글자 약어는 짧은 별칭으로만 남습니다.
4. **하이픈으로 단어를 잇지 않습니다.** 두 낱말이 필요하면 `이름:값`(`-commands:only`).
5. **값이 붙는 옵션은 기본값이 반드시 있습니다**(이름만 쳐도 뜻이 섬).
6. **빼도 동작해야** 옵션입니다. 되돌릴 수 없는 일을 옵션으로 켜지 않습니다(`-proceed`는 새 동작이 아니라 질문을 건너뛰는 절차).

**키워드의 규칙**

1. 그 명령 안에서만 뜻이 섭니다. 같은 낱말이 명령마다 달라도 됩니다.
2. 낱말 전체가 일치할 때만 키워드로 읽습니다(자유 글자 인자를 지키기 위해).
3. 명령 이름과 같은 낱말을 쓰지 않습니다(`CmdCatalog.NameClash()`가 검사). 그래서 곡 이름을 바꾸는 명령은 `retitle`이고 `rename`은 `report`의 키워드입니다. 예외는 곡 낱말 `link` 하나입니다(사용자 요청 — `load link`처럼 링크가 플레이 중인 곡을 가리키므로 `link` 명령과 같은 대상, `CmdCatalog.SharedNames`).
4. **`list`는 나열해 보여 달라**(`help` `option` `keyword` `prompt` `report` `checkpoint`), **`all`은 전부를 대상으로**(`playlist` `id` `title`의 곡, `note`의 노트, `scannow`·`install`의 영역)입니다. `*`는 `all`만 대신합니다. 예외로 `pin all`은 그 곡의 핀을 나열합니다(7절의 통합 계획에 맞춘 것).
5. 상태를 바꾸는 동작은 키워드입니다(`sync rebuild`, `link follow`, `checkpoint next`).
6. **기본 키워드**: 인자 없이 쳤을 때의 뜻이 키워드 하나와 같으면 그 낱말이 기본 키워드이고, 표에 `*`를 붙여 적고(`"list*, last"`) 도움말은 `[list]`처럼 대괄호로 보여 줍니다(글로 쓰지 않음). 기본 키워드는 하나뿐입니다.
7. **필수 키워드 명령**(`report <what>`, `install <what>`, `do <action>`)은 옵션만으로 실행되지 않고, 자기 키워드와 같은 일을 하는 옵션을 두지 않습니다. 키워드 없이 오면 받는 낱말을 알려 주고 멈춥니다.
8. **가변 인자**는 1절의 규칙을 따릅니다.

**중복에 대한 정책**: 같은 일을 두 길로 하는 것(`-og`와 `-origin`, `copy -paste`와 `paste -copy`)은 위반이 아닙니다. 금지는 **같은 말이 명령에 따라 다른 뜻을 갖는 것**입니다.

**어기면 빌드가 실패합니다**: `CmdConsoleTests`의 "Options and keywords keep to their rules"가 위 규칙(이름이 낱말인지·하이픈·기본값·설명·별칭 겹침·표에 없는 옵션·키워드 설명·명령과 키워드의 낱말 겹침·기본 키워드·필수 키워드·설명글의 별칭·`-force`와 `-proceed` 묶음·가변 인자 위치)을 매 빌드에서 검사합니다.

## 3. 명령 목록 (`cmd` 순서)

| 분류 | 사용법 | 옵션 | 키워드 (`[ ]` 기본) | 하는 일 |
| --- | --- | --- | --- | --- |
| 도움말 | `cmd` | -copy | | 모든 명령을 분류별 한 줄로 |
| | `help [<cmd>]` | -copy | `[list]`, last | 명령 설명(인자 없으면 전부, `last`는 바로 앞 명령) |
| | `option [<cmd>]` | -keywords, -copy | `[list]`, last | 옵션 표 또는 그 명령의 옵션 |
| | `keyword [<cmd>]` | -options, -copy | `[list]`, last | 키워드 표 또는 그 명령의 키워드(키워드 낱말 하나를 주면 그 뜻) |
| | `version` | -number \| -check, -copy | | 앱 버전(-check: 적용 날짜·빌드 시각·최신 여부) |
| | `fps` | -restart, -copy | | 앱 자신의 프레임 진단 |
| | `link [<what>]` | -proceed / -copy | start, stop, tiles, follow, unfollow, again, auto, manual | 게임 링크 상태와 조작 |
| | `inspect <what>` | -proceed, -flow, -runs, -copy | start, stop, result | 비주얼라이저 노트로 게임 타이밍 측정 |
| | `log [<what>]` | -copy | `[list]`, all, erase | 앱이 남기는 기록(링크·게임·따라가기·노트) |
| | `install <what>[, …]` | -reinstall, -proceed | all, app, source, linker, maps, edits, data | 링커 설치, 이 버전 부분 재설치 |
| | `scannow [<area>, …]` | -copy | `[all]`, app, source, linker, maps, edits, data | 무결성 검사(결과만) |
| 곡 | `song` | -id \| -title, -copy | | 불러온 곡 정보 |
| | `info <id>` | -title, -copy | now, link | 곡 정보 |
| | `id [<string>]` | -title, -copy | base, custom, edited, main, `[now]`, link, family, origin, all | 곡 id |
| | `title [<string>]` | -id, -copy | 위와 같음, 기본 `[now]` | 곡 제목 |
| | `load <title>` | -id \| -origin \| -slot, -speed, -hidden, -force | origin, edited, link | 곡 불러오기 |
| | `playlist [<string>]` | -id \| -title, -hidden \| -shown, -copy | 위 곡 낱말, 기본 `[all]` | 곡 목록 |
| | `checkpoint [<number>]` | -copy | list, first, next, previous, last | 체크포인트로 시작 위치 옮기기 |
| | `go <tile>` | | | 타일 번호로 시작 위치 옮기기(Tab = 핀) |
| | `pin [<id>] <tile>` | -copy | `[now]`, all | 타일에 핀 꽂기·핀 목록 |
| | `unpin [<id>] <tile>` | | `[now]` | 핀 빼기(Tab = 핀) |
| | `hide <id>` | | now | 곡 숨기기 |
| | `show <id>` | | now | 숨긴 곡 다시 보이기 |
| 수정 | `edit [<id>]` | -proceed | `[now]` | 수정 창 열기 |
| | `note [<id>] <range>` | -proceed, -json, -copy | `[now]`, all | 노트 레인 출력 |
| | `override <json>` | -proceed | | 수정 창 레인을 JSON으로 교체 |
| | `retitle <title>` | -proceed | | 불러온 곡의 표시 이름 |
| 곡 파일 | `sync [rebuild]` | -proceed | rebuild | 맵 동기화·재구축 |
| | `remove <id>` | -proceed | now | 수정본 제거(보관) |
| | `restore` | -proceed | | 마지막으로 제거한 수정본 복구 |
| | `delete <id>` | -proceed | now | 곡 완전 삭제 |
| 설정 | `get <setting>` | | | 설정 값 |
| | `set <setting> <value>` | | | 설정 바꾸기 |
| | `speed [<percent>]` | -rate | | 배속(또는 노트 이동 시간) |
| | `autosave [on \| off]` | | on, off | 설정 자동 저장 |
| | `property [<section>[.<setting>]]` | -copy | | 구역·설정·값 목록 |
| | `export` | -copy | | 설정 JSON |
| | `import <json>` | -proceed | | 설정 JSON 적용 |
| | `rollback` | -proceed | | 마지막 설정 변경 되돌리기 |
| | `reset` | -proceed | | 설정 초기화 |
| | `do <action>` | | clear-previous-checkpoint-key, clear-next-checkpoint-key, clear-settings-key, map-folder, data-folder, view-playlist-details | 설정판의 동작 |
| 콘솔 | `copy [<count>]` | -commands, -answers, -count, -paste | | 화면 메시지 복사 |
| | `paste [<count>]` | -commands, -answers, -count, -copy | | 화면 메시지 다시 찍기 |
| | `prompt [<title>]` | -copy | `[list]` | 저장된 프롬프트 채우기 |
| | `report <what> [<title>]` | -force \| -proceed, -copy, -log | list, create, write, print, done, undone, ask, state, erase, sweep, rename, colon | 다음 작업용 메모 |
| | `update` | -force \| -proceed, -version | | 저장소에서 새 버전 받기 |
| | `clear` | | | 화면 지우기 |
| | `exit` | -keep | | 콘솔 닫기 |
| | `date` | -copy | | 날짜·시각 |
| | `whoami` | | | 누구인지 |

## 4. 명령별 상세

각 항목의 첫 줄은 콘솔 `help`가 보여 주는 설명을 줄인 것이고, 그 아래가 동작 규칙입니다.

### 도움말

- **`cmd`** — 모든 명령을 분류별로, 사용법(34칸)과 한 줄 요약으로 찍습니다.
- **`help [<cmd>]`** — 명령의 사용법·긴 설명·옵션(뜻 포함)·키워드(뜻 포함, 기본은 대괄호). 인자가 없거나 `list`면 모든 명령, `last`는 바로 앞에 친 명령.
- **`option [<cmd>]`** — 인자가 없거나 `list`면 옵션 표를 한 번(명령마다가 아님), 명령을 주면 그 명령의 옵션. `-keywords`면 키워드까지.
- **`keyword [<cmd>]`** — 인자가 없거나 `list`면 키워드와 그것을 받는 명령의 표, 명령을 주면 그 명령의 키워드, **키워드 낱말 하나**를 주면 그 뜻과 받는 명령(명령마다 뜻이 다르면 줄마다). `-options`면 옵션까지.
- **`version`** — 앱 버전만 **바로** 찍습니다(`Visualizer 1.0.5`, 사용자 규칙: 저장소에 묻느라 기다리지 않게). `-check`는 예전 동작으로, 적용 날짜·빌드 시각(실행 파일의 시각)을 더하고 저장소에 물어 **최신인지** 알립니다(`update.ps1 -Check`의 `git fetch`라 잠시 걸림). `-number`는 숫자만 찍고 묻지 않습니다. `-number`와 `-check`는 함께 쓸 수 없습니다. 적용 날짜는 빌드에 적힌 릴리스 날짜이며 맵 캐시의 갱신 시각이 아닙니다. 파서가 바뀐 릴리스는 최신 앱에서도 Map Sync 후 곡을 다시 불러와야 새 타이밍을 씁니다.
- **`fps`** — 앱이 자기 프레임만 재는 진단: 도착 간격과 합성기 간격, 앱이 한 프레임에 쓴 시간, GC 횟수, 렌더 티어, 원인을 짚는 마지막 줄(MMCSS·GC 지연 모드 상태 포함). `-restart`는 찍은 뒤 측정을 새로 시작합니다. 게임이나 시스템에서 읽는 값은 없습니다.
- **`link [<what>]`** — 인자 없으면 읽기 전용 링커가 보내는 상태(읽기 간격·지연의 흔들림·게임 상태·배속·레벨·상태 변화, `Mod`·`Auto` 줄). `start` 파이프 열기 · `stop` 닫기 · `tiles` 게임의 타일 시각과 불러온 곡 대조 · `follow` 게임 시계 따라가기 · `unfollow` 그만 · `again` 측정 새로 · `auto` 게임이 옮긴 곡을 불러오고 스스로 대조·따라가기 · `manual` 그만. 게임에 아무것도 보내지 않고 저장하지 않습니다. 따라가기의 거절 규칙(곡 중간부터 준비됨·배속≠100%·타일 불일치)은 `docs/game-sync-plan.md`. **`stop`은 link auto도 끕니다**(manual로 돌아옴, 사용자 규칙: 링크가 닫히면 스스로 맞출 것이 없음). 파이프를 다시 열어도 auto는 꺼진 채입니다.
- **`inspect <what>`** — 측정 전용 독립 명령. `start`는 물어본 뒤 비주얼라이저 노트대로 레인 키를 게임에 보내고, `stop`·Esc는 모두 떼며 종료합니다. 링크 켜짐·link auto 꺼짐·같은 곡·게임 실패 방지 모드가 필요하고, 게임이 전경일 때만 키를 보냅니다(AGENTS 안전 규칙 6). 카운트다운 중에는 설정의 8개 레인 키와 Space·Enter를 한 번씩 사전 입력합니다(40ms 간격·25ms 탭, 첫 노트 40ms 전 마감, 늦게 시작하면 들어갈 키만). **`-flow start`**(`-f` 별칭)는 키·노트·레인·곡 시각(ms)·게임 타일·늦은 정도(ms)와 문제 판정을 한 줄씩 출력하며 **종료까지 `Visualizer>`를 내놓지 않고 입력을 버립니다**. 콘솔을 닫았다 열어도 진행 중 잠금은 유지됩니다. 측정이 끝나거나 Esc로 멈추면 마지막 판정·종료 이유를 출력하고 프롬프트가 돌아옵니다. 이미 측정 중이면 새 실행을 거절합니다. **`result`**는 마지막 결과, **`-runs:3 result`**는 최근 세 개, **`-runs:* result`**는 남은 전체(기본 1)입니다. 결과는 메모리만 사용하며 앱 종료 때 사라집니다. `-flow`는 start, `-runs`는 result에만 씁니다. 기존 -inspect 옵션은 제거했습니다.
- **inspect 재시작**: 새 inspect는 앱에 남은 일시정지를 풀고 게임 시계에 맡깁니다. 이전 재생 위치의 지나간 노트만으로 새 측정을 끝내지 않습니다. 게임이 60ms 넘게 뒤로 되감기면 이전 키·예약을 해제하고 그 위치부터 노트를 다시 계획하며 사전 입력도 다시 준비합니다(작은 시계 보정은 노트를 반복하지 않음). 체크포인트가 숨긴 앞 노트는 보내지 않고, 사전 입력의 마감은 다음 표시 노트를 기준으로 정합니다.
- **`log [<what>]`** — 앱이 실행되는 동안 메모리에 남기는 기록(사용자 요청: 플레이 중 노트가 사라진 경위를 나중에 읽기 위해). 바뀐 것만 한 줄씩, 시각(밀리초까지)과 함께: 파이프 열림·닫힘, 링커 연결(**연결된 게임 프로세스 id**와 실행 중인 게임 프로세스 수·id — 얼불춤이 두 번 실행되는 버그를 알아보려고, 10초마다 다시 셈), 링커 버전, 레벨(경로·타일 수), 게임 상태·일시정지·게임 오버, follow 상태와 오프셋, auto의 시도 결과, 곡, 노트 준비(개수·시작 타일·배속), 재생 상태, 메인 화면 메시지, 0.5초 넘는 프레임 공백, 그리고 게임이 플레이 중이거나 follow 중이면 1초마다 지금(게임 상태·타일·위치 | 노트 시각·재생 상태·follow·화면에 있는 노트 수). 최근 5000줄만, 앱을 끄면 사라지고, 누른 키는 남기지 않습니다. 인자 없으면 최근 40줄, `all`은 남은 모든 줄, `erase`는 비우기. `report -log create|write <title>`이 메모에 씁니다. 앱이 최소화되어 프레임이 없으면 그동안은 남지 않습니다(그 공백이 한 줄로 남음).
- **`install <what>[, …]`** — `install linker`는 앱에 들어 있는 링커(앱 옆 `plugins\linker\`)를 게임의 `Mods\AdofaiVisualizerSync\`에 씁니다. Unity Mod Manager가 있는 게임 폴더에서만, **어느 폴더에 쓸지 적고 [Y/n]**을 묻고, 두 파일만 쓰며, 같은 버전이 있으면 그대로 둡니다(다른 버전이면 바꿀지 물음). **`-reinstall`**은 이 버전대로 다시 씁니다: `source`(저장소 파일을 이 버전으로 되돌림 — 바뀔 파일 목록을 먼저 보여 주고 **언제나 묻고 `-proceed` 불가**) · `app`(Release 다시 빌드) · `linker`(링커를 다시 씀, 빌드 없음, 앱은 그대로) · `maps`(= `sync rebuild`) · `all`·`*`(source·app·설치된 링커). `source`·`app`은 앱을 닫고 `update.ps1 -Reinstall`이 한 뒤 다시 엽니다. **`edits`·`data`는 사용자의 것이라 재설치하지 않고** 무엇을 쓰면 되는지만 알려 줍니다. 링커가 없는데 `-reinstall linker`면 `install linker`부터 하라고 합니다. 키워드 없이 오면 받는 낱말만 알려 줍니다.
- **`scannow [<area>, …]`** — 영역마다 온전한지 검사해 **결과만** 찍고 아무것도 바꾸지 않습니다. `app` 이 빌드와 그 소스·언어·테마 파일 · `source` 저장소 파일 대 이 버전(`update.ps1 -Scan`, git status) · `linker` 설치된 링커 대 이 버전 · `maps` 맵 사본 대 곡 목록 · `edits` 모든 수정본(사본·레인·원본 해시) · `data` 설정과 곡 목록·id·표시·메모·핀 파일. 기본 `all`. 링커가 설치되지 않았으면, 콕 집었을 때는 `install linker`부터 하라고, 전체 검사에서는 한 줄로 알립니다. 문제가 있으면 맨 끝에 `install -reinstall …`을 권합니다.

### 곡

- **`song`** — 불러온 곡 정보(`info now`와 같음). `-id`·`-title`은 그것만.
- **`info <id>`** — id·제목·아티스트·제작자·종류·길이·노트 수(누르는 노트)·롱노트·자동 노트·난이도·체크포인트 수·슬롯(원곡은 origin)·숨김·경로.
- **`id [<string>]`·`title [<string>]`** — 곡 묶음의 id·제목. 수정본은 슬롯을 붙입니다. 기본 `now`. `id -title`·`title -id`는 둘 다.
- **곡 묶음 낱말**: `base` 기본 맵 · `custom` 커스텀 · `edited` 수정본 · `main` 메인 화면 목록(그 순서) · `now` 불러온 곡 · `link` 게임에서 플레이 중인 곡(링크가 켜져 있고 링커가 연결됐을 때만, `link auto`처럼 원곡 우선 — 아니면 이유를 알리고 멈춤) · `family` 불러온 곡의 원곡과 모든 수정본 · `origin` 불러온 곡의 원곡 · `all` 모든 곡.
- **`load <title>`** — 5절.
- **`playlist [<string>]`** — 곡 묶음의 `id · 종류 · 제목`. 기본 `all`. `-hidden`은 숨긴 곡만, `-shown`은 보이는 곡만, `-id`·`-title`은 그것만.
- **`checkpoint [<number>]`** — 불러온 곡의 시작 위치를 체크포인트로 옮깁니다(메인 화면 목록에서 고른 것과 같음: 정지 상태로 다시 준비, 구간 칸은 비움, 게임 따라가기는 끝남). 번호는 **메인 화면이 붙이는 번호 그대로**(곡 처음은 번호가 아니라 "the beginning"). `first` 곡 처음 · `last` 마지막 · `next`·`previous` 한 칸씩, **양 끝에서 순환**. 인자 없으면 지금 자리(`… starts at checkpoint 2 (tile 120), of 6.`), `list`는 목록(쓰는 자리에 `>`). 곡이 없거나 체크포인트가 없는 맵이면 이유만.
- **`go <tile>`** — 불러온 곡을 그 타일부터 준비합니다(구간 칸과 같은 규칙: 그 타일 이후 첫 노트부터, 배치는 곡 처음부터 만든 것을 잘라 씀). 1 이상의 정수만 받고, 그 곡에 없는 타일·중간 회전 타일·롱노트 도중이면 거절합니다(파서의 예외 대신 콘솔이 같은 검사를 콘솔 문구로). **Tab은 그 곡의 핀 타일**을 내놓습니다. 처음으로 돌아가려면 `checkpoint first`.
- **`pin [<id>] <tile>`** — 곡의 타일에 핀을 꽂아 `go`의 Tab 후보로 둡니다. **핀은 곡 묶음(family)에 속합니다**: 원곡에 꽂든 수정본에 꽂든 원곡과 모든 수정본이 같은 핀을 봅니다(사용자 규칙). 타일 순서로 정렬되고, `go`가 받을 수 있는 타일만 꽂힙니다. 기본 `now`. `pin all`(또는 `pin <id> all`)은 `pin 1 · tile 120`처럼 번호를 붙여 나열합니다. 이미 있는 타일이면 그렇다고만. 앱 데이터 폴더의 `pins.json`에 저장되며 수정 창이 열려 있어도 됩니다(노트를 준비하지 않으므로).
- **`unpin [<id>] <tile>`** — 그 곡 묶음에서 핀을 뺍니다. 기본 `now`. Tab은 그 곡의 핀(곡 id를 앞에 주면 그 곡의 핀).
- **`hide <id>`·`show <id>`** — 곡 목록에서 숨기기·되돌리기(`songs.json`에 저장).

### 수정

- **`edit [<id>]`** — 수정 창을 엽니다. 인자 없으면 불러온 곡. 수정 창은 하나뿐이라, 다른 곡으로 부르면 열린 창을 앞으로 가져오고 거절합니다.
- **`note [<id>] <range>`** — 곡의 노트를 **재생될 모양 그대로**(지금 설정의 배치 규칙, 수정본이면 그 레인을 덮어) `n<번호> <레인> <다음 노트까지 ms>`로 찍습니다. 동타는 `n3-4 3-4`. 기본 `now` = 지금 화면: **수정 창이 열려 있으면 저장 여부와 상관없이 수정 중인 레인**, 아니면 불러온 곡. id를 주면 불러오지 않고 그 곡을 찍습니다. 범위: `all`(`*`) · `a..b` · `a..` · `..b` · `n`, `-1`은 마지막(범위 끝에도). **번호는 맵의 hit 번호**라 자동 타일·롱노트 끝도 번호를 차지합니다. 화면에 없는 레인도 그대로 찍습니다. `-json`은 `{ "notes": [{ "n", "tile", "ms", "chord" }] }`(`ms`는 곡 안의 시각, `chord`는 같은 동타가 공유하는 번호)로, `override`가 그대로 읽습니다. 적용하지 않은 설정이 있으면 먼저 적용할지, 곡을 불러오지 않았지만 메인 화면에 골라 둔 곡이 있으면 불러올지 묻습니다(`-proceed`는 둘 다 Y).
- **`override <json>`** — 수정 창이 열려 있을 때만, `note -json` 결과로 레인을 통째로 바꿉니다. 번호와 레인만 반영하고 `ms`는 대조용입니다. 한 줄이라도 어긋나면(시각 불일치·화면 밖 레인·떨어진 레인의 동타·한 레인에 50ms 안 두 노트) 아무것도 바꾸지 않고 첫 문제를 알립니다. 수정 창의 되돌리기로 취소됩니다.
- **`retitle <title>`** — 불러온 곡의 표시 이름(곡 목록에서 바꾸는 것과 같음). 수정본은 `n*` 접두어를 지키고, 이름은 원곡에 저장되어 모든 수정본이 따릅니다. 전후를 보여 주고 [Y/n]. 이름에는 글자나 숫자가 하나 이상 있어야 합니다.

### 곡 파일

- **`sync [rebuild]`** — 맵 폴더의 곡을 커스텀으로 복사하고 앱의 세 폴더를 다시 읽습니다(메인 화면의 동기화와 같음, 묻지 않음). `rebuild`는 커스텀을 처음부터 다시 만들며(맵 폴더에서 빠진 곡이 목록에서 사라지는 유일한 길) 먼저 묻습니다. 둘 다 진행률 막대(8절).
- **`remove <id>`** — 수정본을 슬롯 폴더째 `maps/removed/`로 보관합니다(한 곡만 보관, 다음 `remove`가 앞의 보관본을 지움). [Y/n].
- **`restore`** — 마지막으로 `remove`한 수정본을 되돌립니다(원래 슬롯이 비어 있으면 그 슬롯, 아니면 첫 빈 슬롯). 앱을 다시 켜도 됩니다.
- **`delete <id>`** — 곡을 완전히 지웁니다(`restore` 불가). 수정본은 슬롯 폴더째, 커스텀 맵은 다음 동기화에 돌아옵니다. 불러온 곡이면 화면에서도 내립니다. [Y/n].

### 설정

- **`get <setting>`** — 값(`get general.language`). 이름은 9절.
- **`set <setting> <value>`** — 바꾸고 바로 적용합니다(`rollback`으로 되돌림). 토글은 `true/false`·`on/off`·`1/0`·`able/disable`, 비울 수 있는 키는 `none`, 데이터 폴더 기본값은 `auto`, 공백이 있는 값(패턴·색·경로)은 줄 끝까지. 설정판에서 잠기는 항목은 `set`도 거부합니다(예: `tiles.instant-down`이 false면 `tiles.down-notes`, 콘솔 쪽 판단은 `SettingPaths.LockedBy`).
- **`speed [<percent>]`** — 배속(`set timing.speed`와 같음). 값이 없으면 지금 값. `-rate`는 배속 대신 **노트 이동 시간**(`display.travel`, ms라 작을수록 빠름).
- **`autosave [on | off]`** — 적용한 설정을 저장소에도 쓸지(`general.autosave`). 값이 없으면 지금 값. 설정 바로가기 명령(`speed`·`autosave`)은 모두 값 없이 치면 보여 줍니다.
- **`property [<section>[.<setting>]]`** — 인자 없으면 구역 목록, 구역이면 그 설정과 값, 설정이면 받는 값.
- **`export`** — 설정 JSON(설정판의 내보내기와 같음, 맵·데이터 폴더 제외).
- **`import <json>`** — 적힌 설정만 적용하고 나머지는 그대로. 바뀔 것을 보여 주고 묻습니다. 두 폴더 경로는 이 PC의 값을 유지합니다. 없어진 설정 이름은 조용히 건너뜁니다.
- **`rollback`** — 마지막 설정 변경을 무엇이 했든(설정판·명령·불러올 때의 배속 초기화·여분 레인 같은 강제 변경) 되돌립니다. 그 단계에서 **바뀐 항목만** 돌리고, 다시 치면 한 단계 더. 앱을 끄면 기록은 사라집니다.
- **`reset`** — 모든 설정을 기본값으로(맵·데이터 폴더는 그대로, `rollback`으로 되돌림).
- **`do <action>`** — 설정판의 동작: 이전·다음 체크포인트 키·설정 키 지우기, 맵 폴더·데이터 폴더 고르기(폴더 고르기 창), 곡 리스트 상세 창 열기. 키워드 없이 오면 받는 낱말만.

### 콘솔

- **`copy [<count>]`** — 화면의 마지막 메시지들을 클립보드로. 숫자는 그만큼 앞까지, `*`(또는 `all`)은 전부, `-1`은 첫 메시지, `a..b`는 범위. `-commands`(기본 `with`)는 친 명령을 `Visualizer>`와 함께, `-commands:only`는 친 명령만, `-answers`(기본 `skip`)는 답한 질문을 빼고 `:with`면 넣음, `-count`(기본 `others`)는 셀 때 복사 명령 자신을 세지 않고 `:all`이면 셈, `-paste`는 복사하면서 찍기도.
- **`paste [<count>]`** — 같은 범위를 화면에 다시 찍습니다. `-copy`면 복사까지. 프롬프트 빈칸에서는 `/paste`로 값을 넣습니다.
- **`prompt [<title>]`** — 언어 모델에 붙여 넣을 프롬프트를 채웁니다(6절). 인자 없거나 `list`면 제목 목록. 아무 곳에도 보내지 않습니다.
- **`report <what> [<title>]`** — 7절.
- **`update`** — 저장소에 이 복사본이 최신인지 묻고, 최신이면 그렇다고 끝납니다. 새 버전이 있으면 대상 버전과 재시작을 알리며 [Y/n](`-proceed`로 건너뜀), 예라면 앱을 닫고 `update.ps1`이 받기 → Release 빌드 → 앱 다시 열기를 하며, **링커가 설치되어 있으면 그 버전의 링커도 다시 씁니다**(질문에 적음). 이 복사본에 자기 수정이 있거나 앞서 있으면 거절하고 `-force`를 알려 줍니다. `-force`는 자기 수정을 버리고 가고, `-version:1.0.0`(`-v:1.0.0`)은 그 버전으로 갑니다(최신이 아닌 버전은 `-force` 필요) — **언제나 묻고** 위험은 사용자 책임이라고 적습니다. 없는 버전은 오류만(목록 보기 없음). 곡·설정·메모는 건드리지 않습니다. **저장소에 새 커밋이 없어도 실행 중인 앱이 이 복사본의 파일보다 오래된 빌드면**(소스 버전이 다르거나 빌드 뒤에 바뀐 `src` 파일이 있으면, `scannow app`과 같은 검사) 그렇다고 알리고 다시 빌드할지 묻습니다(사용자 요청 — 코드를 고치는 이 복사본은 `update`만으로는 새 빌드를 받지 못했음). 예라면 `install -reinstall app`과 같은 길로 앱을 닫고 빌드해 다시 열며, 이 복사본의 자체 변경도 함께 빌드된다고 질문에 적고, 설치된 링커가 이 복사본의 링커보다 오래됐으면 함께 다시 씁니다. `version -check`의 최신 여부 줄도 이 경우를 말합니다.
- **`clear`** — 화면 지우기. **`exit`** — 화면을 지우고 콘솔 닫기(`-keep`은 다음에 열 때를 위해 화면을 남김, 창을 닫는 것과 같음). **`date`** — 이 컴퓨터의 날짜·시각. **`whoami`** — 그림 한 장.

## 5. load

- 제목의 **어느 부분이든** 맞으면 됩니다(대소문자 무관). 대상은 원곡(기본·커스텀)이며, 맞는 원곡마다 그 **수정본을 바로 아래에** 붙여 한 목록으로 보여 주고 번호로 고릅니다. 원곡 하나만 맞고 수정본이 없으면 바로 불러옵니다. 목록이 100개 이상이면 출력하지 않고 제목을 더 적어 달라고 합니다.
- `*`는 모든 곡, `.`은 불러온 곡(다시 불러오거나 옵션과 함께 쓸 때), `link`는 게임에서 플레이 중인 곡의 원곡(원곡이 목록에 없으면 수정본, 링크가 켜져 있을 때만). `load -slot:<n> link`는 그 곡의 n번 수정본, `-origin`·`-slot`은 `.`과 같이 씁니다(옵션은 제목 앞에).
- 수정본은 목록 제목 그대로(`1*Plum - R`) 정확히 치면 바로 불러옵니다(자동완성에는 없음).
- `-origin` 원곡만, `-slot` 제목이 맞는 수정본만(원곡이 사라진 수정본 포함), `-slot:<n>` 그 슬롯의 수정본만. 하나면 바로, 여럿이면 목록.
- `-id <id>`: 8자 id가 정확히 같아야 합니다(두 번째 단어는 받지 않음). 제목에 맞는 곡이 없고 친 글자가 id 모양(8자, 첫 자리 0–2)이면 id로도 찾아봅니다.
- 키워드: `load origin` 불러온 수정본의 원곡(원곡이면 거절, 수정본이 있을 때만 `load edited` 안내) · `load edited` 불러온 원곡의 수정본 목록(하나면 바로, `-slot:<n>`과 함께면 그 슬롯).
- 숨긴 곡은 결과에서 빠지고 `-hidden`을 주면 함께 나옵니다.
- **배속**: 불러올 때 배속을 100%로 되돌리고 `timing.speed: 80 → 100`을 알립니다(고급 설정 **불러오기 배속 유지**가 켜져 있으면 유지). `-speed`(`:keep`)는 유지, `-speed:reset`은 초기화이며 설정과 무관하게 따릅니다. 되돌림은 `rollback` 한 단계입니다.
- **`-force`**: Unsupported 곡에만 동작하며 언제나 [Y/n]. 곡의 형식보다 낮은 검증 형식부터(없으면 높은 것) 읽어 보고, 읽히면 캐시에 그 형식을 남기며 불러옵니다(`Forced: read as format version N.`, 곡 목록에 주황 `forced`). 모든 형식이 실패하면 그대로 둡니다. 다른 곡에 붙이면 그렇다고 알리고 그냥 불러옵니다. 근거: `docs/debug-logs/unsupported-maps.md`.
- 불러오기 전에 설정판을 닫습니다(9절의 공통 규칙). 메인 화면 목록에 있는 곡이면 검색창에도 골라집니다.

## 6. prompt의 빈칸 채우기

프롬프트에 빈칸이 있으면 `prompt <title>`이 하나씩 묻습니다. 줄은 `    prompt:<제목> (1/2)>`로 들여 써서 콘솔의 `Visualizer>`와 구분되고, 친 줄은 명령이 아니라 **그 빈칸의 값**입니다.

| 입력 | 뜻 |
| --- | --- |
| 아무 글자 | 그대로 값(`/string` 생략) |
| `/string <글자>` | 글자 그대로(`/`로 시작하는 값을 넣을 때) |
| `/<명령>` | 그 명령의 **출력을 값으로**(`/paste`, `/note -json 1..64`) — 찍고 끝나는 명령만 받고, 질문·창을 띄우는 명령은 거절 |
| 빈 줄 | 같은 빈칸을 다시 물음 |
| `/skip` | 비워 둠(`>>> WRITE THE REQUEST HERE. … <<<`가 남음) |
| `/back` · `/again` | 이전 빈칸으로 · 처음부터 |
| `/cancel` | 취소(채운 내용은 한 번 찍어 둠) |

다 채우면 줄인 미리보기와 `[Y/n/detail]`을 묻고(`detail`은 전부 찍은 뒤 다시 [Y/n]), Y면 클립보드로 복사합니다. 지금 저장된 프롬프트는 `note-lanes` 하나입니다.

## 7. report — 메모 편집기

에이전트를 쓸 수 없을 때 문제를 적어 두는 곳입니다. 앱 데이터 폴더의 `reports/<제목>.txt`에만 쓰고 **누가 지울 때까지 남습니다**(버전으로 지우지 않음).

- **낱말이 필수**: `create <제목>` 새로 만들어 열기(같은 제목이 있으면 거절, `-force`면 묻고 나서 빈 채로 열고 저장해야 바뀜) · `write <제목>` 있는 메모를 열어 덧붙이기(없으면 거절) · `list [<글자>]` 목록(제목이나 본문에 그 글자가 든 것만, 맨 끝 `n of m still open`) · `print <제목>` 열지 않고 찍기 · `done`·`undone <제목>` 반영 표시 · `ask <제목> > <이유>` 해결 못 함과 이유(설정의 언어로) · `state <제목>` 한 줄(`done 1.0.1` · `open` · `asked: <이유>`) · `erase <제목>` 지우기([Y/n]) · `sweep` 반영된 것 모두 지우기([Y/n]) · `rename <옛 제목> > <새 제목>`([Y/n]) · `colon` 편집기 `:` 명령. 제목이 낱말과 같으면 따옴표(`report write "create"`). 제목은 글자·숫자·공백·`-`·`_`만.
- **자리 셋**: open(기다리는 중) · done(반영됨, 그 버전과 함께) · asked(이유와 함께 답을 기다림). 표시는 `reports/done.mark`, 작성 버전은 `reports/written.mark`에 있어 본문에 타이핑해 넣거나 지울 수 없습니다. **메모를 다시 저장하면 저절로 open**이 됩니다.
- **편집기**: 콘솔 창 안에 층으로 뜹니다. `i` 쓰기 · Esc 보기(쓰기 중 Enter는 줄바꿈, Tab은 공백 넷). 보기에서 `:`를 누르면 상태줄이 명령줄: `:w` 저장 · `:q` 나가기(변경이 있으면 상태줄 [Y/n]) · `:wq`(`:x`) · `:copy` 고른 글(없으면 전체) 복사 · `:d`(`:delete`) 제거. 상태줄은 제목·줄 수·커서·`[+]`(저장 안 함)과 오른쪽 키 안내. [Y/n]은 글자를 치고 Enter. 한글 입력이 되고 Ctrl+T도 됩니다. 콘솔을 닫아도 쓰던 메모는 남아 다시 열면 그 자리로 돌아오고, 앱을 끌 때만 경고합니다.
- **터미널**: 에이전트는 루트의 `report.ps1`(`open`·`list`·`read`·`state`·`done`·`undone`·`asked <제목> -Why <이유>`)로 같은 일을 합니다. 표시를 바꾸는 길은 이 스크립트와 콘솔뿐입니다.

## 8. 진행률 막대

시간이 걸리는 명령(`sync`, `sync rebuild`, `install linker`, `inspect -flow start`)은 프롬프트 대신 `Map sync  [#########...............]  38%`를 띄우고, **끝날 때까지 `Visualizer>`를 내놓지 않아** 다른 명령을 칠 수 없습니다. 그동안 Ctrl+T와 복사만 통하고, 들어온 입력은 쌓지 않고 버립니다. 끝나면 결과 줄을 적고 프롬프트가 돌아옵니다. 메인 화면 설명줄도 같은 퍼센트를 적습니다(복사 40% + 세 폴더 파싱 60%).

## 9. 공통 동작 규칙

- **설정판 먼저 닫기**: `load` `checkpoint` `go` `hide` `show` `edit` `set` `speed` `autosave` `import` `rollback` `reset` `do`는 설정판이 열려 있으면 닫고 실행합니다. 적용하지 않은 변경이 있으면 [Y/n](Y 적용, n 버림, `-proceed`는 Y). 읽기만 하는 명령은 설정판을 건드리지 않습니다.
- **수정 창이 열려 있을 때**: 콘솔은 그대로 쓸 수 있지만 `load` `set` `speed` `autosave` `import` `reset` `rollback` `do` `sync` `remove` `restore` `delete` `retitle` `checkpoint` `go`는 "The note editor is open, so <명령> cannot run. Save or close the editor first."로 거절합니다(노트나 곡을 바꾸기 때문). `note`는 수정 중인 레인을 찍고, `edit`은 열린 창을 앞으로 가져옵니다(다른 곡이면 거절). `pin`·`unpin`은 노트를 바꾸지 않아 됩니다.
- **곡 id**: 첫 자리 폴더(0 기본 · 1 커스텀 · 2 수정본) + 소문자·숫자 7자리(글자 최소 하나). `song-ids.json`에 짝지어 두고, 형식이 맞지 않는 id는 다음 실행·동기화 때 새로 발급합니다. 내부 식별은 맵 경로 해시입니다.
- **저장되는 것**: 설정(`settings.json`, `autosave`가 켜져 있을 때), 곡 목록 선택(`songs.json`), id(`song-ids.json`), 표시와 마지막 곡(`song-marks.json`), 핀(`pins.json`), 메모(`reports/`). 콘솔 기록과 키 입력은 저장하지 않습니다.

## 10. 옵션 표

| 옵션 | 별칭 | 기본값 | 뜻 | 쓰는 명령 |
| --- | --- | --- | --- | --- |
| `-copy` | `-c` | | 출력하고 클립보드에도 복사 | cmd, help, option, keyword, version, fps, link, inspect, log, scannow, song, info, id, title, playlist, checkpoint, pin, note, property, export, paste, prompt, report, date |
| `-proceed` | `-p` | | 모든 질문에 Y로 답하고 진행 | inspect, install, edit, note, override, retitle, sync, remove, restore, delete, import, rollback, reset, report, update |
| `-id` | | | id만 / `title`과 함께면 id도 / `load`는 id로 찾기 | song, title, load, playlist |
| `-title` | `-tl` | | 제목만 / `id`와 함께면 제목도 | song, info, id, playlist |
| `-hidden` | `-h` | | 숨긴 곡만 / `load`는 숨긴 곡도 | load, playlist |
| `-shown` | `-s` | | 보이는 곡만 | playlist |
| `-origin` | `-og` | | 원곡을 바로 불러옴 | load |
| `-slot[:<n>]` | `-sl` | 모든 수정본 | 수정본만 / 번호면 그 슬롯(1–9)을 바로 | load |
| `-speed[:keep\|reset]` | | `keep` | 불러올 때 배속을 어떻게 할지 | load |
| `-force` | | | 거절될 일을 묻고 나서 그대로 함(언제나 물음) | load, report, update |
| `-rate` | `-ar` | | 배속 대신 노트 이동 시간(ms) | speed |
| `-keywords` | `-k` | | 키워드도 | option |
| `-options` | `-o` | | 옵션도 | keyword |
| `-number` | `-n` | | 숫자만 | version |
| `-check` | | | 저장소에도 물어 최신인지 확인(적용·빌드 시각 포함, 잠시 걸림) | version |
| `-restart` | `-r` | | 찍은 뒤 측정 새로 | fps |
| `-keep` | `-m` | | 다음에 열 때 화면을 남김 | exit |
| `-json` | | | JSON으로 | note |
| `-paste` | `-pa` | | 복사하면서 찍기도 | copy |
| `-commands[:with\|only]` | | `with` | 친 명령을 함께 / 친 명령만 | copy, paste |
| `-answers[:with\|skip]` | | `skip` | 답한 질문을 함께 / 빼고 | copy, paste |
| `-count[:all\|others]` | | `others` | 셀 때 복사 명령 자신을 셀지 | copy, paste |
| `-version[:latest\|<version>]` | `-v` | `latest` | 갈 버전 | update |
| `-reinstall` | | | 있어도 이 버전대로 다시 씀 | install |
| `-log` | | | 앱이 남기는 기록 전체 | report (create·write) |
| `-flow` | `-f` | | 실행 종료까지 보낸 키를 순서대로 출력하고 콘솔 입력 잠금 | inspect (start) |
| `-runs[:<n>\|*]` | | `1` | 최근 실행 n개, *면 남은 전체 | inspect (result) |

## 11. 키워드 표

| 키워드 | 뜻 (명령) |
| --- | --- |
| `list` | 나열 (help·option·keyword: 전부 · prompt: 제목 · checkpoint: 체크포인트, 지금 자리에 `>` · report: 메모, 뒤 글자로 거름 · log: 최근 40줄) |
| `last` | 바로 앞 명령 (help·option·keyword) · 마지막 체크포인트 (checkpoint) |
| `all` | 전부 (playlist·id·title: 모든 곡 · note: 모든 노트 · scannow: 모든 영역 · install: 이 버전 전체(`-reinstall`) · log: 남은 모든 줄) · 핀 목록 (pin) |
| `now` | 불러온 곡 (info·id·title·playlist·hide·show·edit·remove·delete·pin·unpin) · 지금 화면, 수정 중이면 수정 중인 레인 (note) |
| `base` · `custom` · `edited` · `main` · `family` · `origin` | 곡 묶음 (id·title·playlist) — 기본 맵 · 커스텀 · 수정본 · 메인 화면 목록 · 원곡과 모든 수정본 · 원곡 |
| `origin` · `edited` | 불러온 수정본의 원곡 · 불러온 원곡의 수정본 (load) |
| `first` · `next` · `previous` | 곡 처음 · 다음 · 이전 체크포인트 (checkpoint) |
| `start` · `stop` · `tiles` · `follow` · `unfollow` · `again` · `auto` · `manual` | 게임 링크 (link), start·stop은 측정 시작·종료 (inspect) |
| `app` · `source` · `linker` · `maps` · `edits` · `data` | 영역 (scannow·install) |
| `rebuild` | 커스텀 맵을 처음부터 (sync) |
| `result` | 측정 결과 (inspect) |
| `on` · `off` | 켜기 · 끄기 (autosave) |
| `create` · `write` · `print` · `done` · `undone` · `ask` · `state` · `erase` · `sweep` · `rename` · `colon` | 메모 동작 (report) · `erase`는 기록 비우기 (log) |
| `clear-previous-checkpoint-key` · `clear-next-checkpoint-key` · `clear-settings-key` · `map-folder` · `data-folder` · `view-playlist-details` | 설정판 동작 (do) |

## 12. 설정 이름 (`구역.설정`)

설정판에 행이 있는 설정만 이름이 있습니다. 값의 꼴: 토글 `true`/`false`(그 밖의 낱말은 4절 `set`) · 키 이름(`R`, `F5`, `Back`, `Space`, `[`, `-` 등) · 비울 수 있는 키는 `none` · 숫자(단위는 표) · 타일 목록 `3 4 5 6` · 색 `R, G, B` 또는 `#RRGGBB` · 폴더는 전체 경로(데이터 폴더는 `auto`도). 고를 수 있는 값은 `property <설정>`이 보여 줍니다. `experimental` 구역은 `advanced.experimental`이 true일 때만 열립니다.

| 이름 | 설정 속성 | 꼴 |
| --- | --- | --- |
| `general.language` | Language | 선택 |
| `general.theme` | Theme | 선택 |
| `general.information-buttons` | ShowGuideButtons | 토글 |
| `general.song-info` | ShowSongInfo | 토글 |
| `general.game-only` | OnlyReactWhenAdofaiForeground | 토글 |
| `general.autosave` | AutoSaveSettings | 토글 |
| `songs.sort` | SongSortKey | 선택 |
| `songs.high-first` | SongSortHighFirst | 토글 |
| `songs.group` | SongGroup | 선택 (`base` · `custom` · `edited` · `base-custom` · `custom-edited` · `base-edited` · `all`) |
| `checkpoint.auto` | AutoCheckpointAdvance | 토글 |
| `checkpoint.auto-on-load` | AutoCheckpointOnLoad | 토글 |
| `keys.reset` · `keys.start` · `keys.toggle` | ResetKey · StartTriggerKey · VisualizerToggleKey | 키 |
| `keys.previous` · `keys.next` · `keys.settings` | PreviousCheckpointKey · NextCheckpointKey · SettingsHotkey | 키 또는 `none` |
| `keys.lane-1` … `keys.lane-8` | LaneKeys | 키 |
| `design.display-lanes` | DisplayLaneCount | 선택 (`max` · 1 · 2 · 4 · 6 · 8, 최대 타일 수보다 작으면 거절) |
| `tiles.max` · `tiles.min` | MaximumTileCount · MinimumTileCount | 선택 |
| `tiles.dynamic` | DynamicTiming | 토글 |
| `tiles.8k-6k` · `tiles.6k-4k` · `tiles.4k-2k` · `tiles.2k-1k` | EightKey… · SixKey… · FourKey… · TwoKeyBoundaryMs | ms |
| `tiles.instant-down` | ImmediateDownshift | 토글 |
| `tiles.down-notes` | DownshiftNoteCount | 노트 수 |
| `tiles.pattern-8k` · `-6k` · `-4k` · `-2k` | EightKeyPattern … TwoKeyPattern | 타일 목록 |
| `tiles.initial` | InitialTile | 선택 |
| `tiles.order-8k` · `-6k` · `-4k` · `-2k` | EightKeyOrder … TwoKeyOrder | 타일 목록 |
| `timing.speed` | PlaybackSpeedPercent | % |
| `timing.chord-window` | SimultaneousWindowMs | ms |
| `timing.no-center-pair` | NoChordCenterPair | 토글 |
| `timing.chord-side` | ChordLanePreference | 선택 (`none` · `1-2` · `3-4` · `1-2,3-4`) |
| `timing.trill-window` | TrillWindowMs | ms (0이면 트릴 없음) |
| `timing.no-trill-center-pair` | NoTrillCenterPair | 토글 |
| `timing.trill-side` | TrillLanePreference | 선택 (chord-side와 같은 값) |
| `timing.start-delay` | StartDelayMs | ms |
| `timing.calibration` | CalibrationNoteCount | 노트 수 |
| `timing.pause-align` | AlignNextNoteOnPause | 토글 |
| `judgment.bar` | JudgmentBarMode | 선택 |
| `judgment.window` | JudgmentBarDifficulty | 선택 |
| `judgment.average` | ShowJudgmentAverage | 토글 |
| `judgment.average-notes` | JudgmentAverageCount | 노트 수 |
| `magic.notes` | MagicCircleNoteCount | 노트 수 |
| `magic.max-interval` · `magic.fade-in` · `magic.lead` · `magic.fade-out` | MagicCircleMaxIntervalMs · …FadeInMs · …LeadMs · …FadeOutMs | ms |
| `magic.chords` | MagicCircleChords | 토글 |
| `magic.fixed` | MagicCircleFixedTiles | 토글 |
| `magic.fixed-count` | MagicCircleTileCount | 선택 |
| `display.travel` | NoteTravelTimeMs | ms |
| `display.line-height` | JudgmentLineHeightDip | px |
| `display.press-window` | KeyPressWindowMs | ms |
| `display.thickness` · `display.width` · `display.hold-width` | NoteThickness · NoteWidth · HoldNoteWidth | 배 |
| `display.offset` | GlobalDisplayOffsetMs | ms |
| `display.input-effect` · `display.miss-effect` | ShowInputEffect · ShowMissEffect | 토글 |
| `colors.lane-effect` · `colors.note` · `colors.chord` · `colors.start-note` · `colors.end-note` · `colors.checkpoint` · `colors.judgment-line` · `colors.miss` | LaneEffectColor · NoteColor · ChordColor · StartNoteColor · EndNoteColor · CheckpointColor · JudgmentLineColor · MissEffectColor | 색 |
| `colors.pressed-transparency` | PressedNoteTransparencyPercent | % |
| `advanced.experimental` | ExperimentalFeatures | 토글 |
| `advanced.extra-lanes` | ExtraLanes | 토글 |
| `advanced.slowdown` | SlowNotesPastJudgmentLine | 토글 |
| `advanced.tile-order` | EnableTilePreference | 토글 |
| `advanced.chord-lanes` | ChordSectionLanes | 토글 |
| `advanced.keep-speed` | KeepSpeedOnLoad | 토글 |
| `advanced.next-gap` | NextIntervalTiles | 토글 |
| `advanced.both-gaps` | BothIntervalTiles | 토글 (켜면 next-gap보다 우선) |
| `advanced.map-folder` | MapRootDirectory | 폴더 |
| `advanced.data-folder` | DataRootDirectory | 폴더 또는 `auto` |
| `advanced.process` | AdofaiProcessName | 글자 |
| `experimental.chord-before` | ChordAvoidBeforeLanes | 토글 |
| `experimental.run-restart` | RestartPatternOnRun | 토글 |
| `experimental.enhanced-chords` | EnhancedChordRules | 토글 |

새 설정을 추가하면 `SettingPaths`에 이름을 넣어야 `CmdConsoleTests`의 "every setting has a name" 검사를 통과합니다.

## 13. 계획 중인 변경 (아직 만들지 않음)

명령 체계의 큰 개편을 앞두고 결정만 해 둔 것들입니다.

- **`list`를 `all`로 통합**(사용자 결정): 두 낱말의 경계가 모호해서 하나로 합칩니다. 규칙은 "`all`(`*`)은 그 명령이 다루는 것 전부이고, 하나에만 할 수 있는 일을 하는 명령(`checkpoint`·`prompt`·`report`)에서는 전부를 보여 준다". `help`·`option`·`keyword`·`prompt`의 `list*` → `all*`, `checkpoint`·`report`의 `list` → `all`, `copy`·`paste`는 `all`을 정식 키워드로(지금도 범위로는 받음). 예전 `list`를 치면 "이제 `all`"이라고 한 줄 알려 줍니다. `pin all`은 이미 이 방식입니다.
- **`report find <글자>`**(사용자 결정): `report list <글자>`의 거르기를 동작 낱말로 분리합니다.
- **중복 명령 정리안**과 **가변 인자 후보**: `docs/future-plans.md` 2·3절.
