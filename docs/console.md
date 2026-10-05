# 명령어 콘솔 (앱 0.5.1, 1.0.0 갱신)

Ctrl+T로 여는 콘솔의 명령·옵션·키워드·설정 이름을 모은 문서입니다. 콘솔이 열려 있을 때 Ctrl+T는 콘솔만 최소화하고, 한 번 더 누르면 다시 보입니다(0.6.3; 메인·설정·콘솔 창에서 받고, 메인 창은 그대로). 코드의 원본은 `src/Visualizer.App/Commands/`
(`CmdCatalog.cs` 명령 표·옵션·키워드·파서, `SettingPaths.cs` 설정 이름, `CmdSession.cs` 동작)이며, 콘솔이 출력하는 문구는
앱 언어와 상관없이 모두 영어입니다(곡 제목은 데이터라 그대로).

## 옵션과 키워드의 원칙 (앱 0.9.2, 사용자 규칙 — **새 명령은 이 절을 지켜야 합니다**)

**옵션 `-name`은 부사입니다.** 명령이 *하는 일은 그대로 두고* 결과의 표현·범위·절차만 바꿉니다. 전역 표에 올라가 **어느 명령에 붙어도 뜻이 같고**, 빼도 명령이 성립합니다.
**키워드는 목적어·동사입니다.** 인자 자리에 오는 정해진 낱말로, 그 명령의 **대상**이나 **동작**을 고릅니다. 명령마다 뜻이 달라도 됩니다.

어느 쪽인지는 네 가지로 가릅니다.

1. 그 말이 없으면 **무엇을 할지 정해지지 않는가** → 키워드 (`link start`, `install linker`, `report create`)
2. 명령이 **다루는 대상**을 고르는가 → 키워드 (`load origin`, `checkpoint last`)
3. **다른 명령에 붙여도 같은 뜻**인가 → 옵션 (`-copy`, `-proceed`)
4. **출력 모양·범위·질문 생략**만 바꾸는가 → 옵션 (`-title`, `-hidden`, `-json`)

### 옵션의 규칙

1. **이름 하나에 뜻 하나** — 전역입니다. 명령마다 뜻이 달라지면 옵션 자격이 없습니다. 여러 명령이 쓰는 `-force`도 뜻은 하나입니다: **거절될 일을 묻고 나서 그대로 합니다**(`load`는 지원하지 않는 곡, `report create`는 이미 있는 제목, `update`는 자기 수정이 있는 복사본·최신이 아닌 버전).
   - **`-force`는 언제나 [Y/n]을 묻습니다**(1.0.0, 사용자 규칙). `-proceed`가 대신 답할 수 없어 둘을 같이 쓰면 오류입니다(`-force always asks first, so -proceed cannot answer for it.`). 두 옵션을 다 가진 명령은 둘을 한 묶음(`[-force | -proceed]`)에 둡니다.
2. **이름은 낱말로** 적습니다(`-origin`, `-speed`). 두 글자 약어는 예전 입력을 살리는 **짧은 별칭**으로만 남고(`-og`, `-sm`), **도움말·Tab·오류 문구·설명글은 낱말만** 씁니다.
3. **하이픈으로 단어를 잇지 않습니다.** 두 낱말이 필요하면 **`이름:값`**으로 적습니다 — `-speed:keep`, `-commands:only`, `-slot:3`. `=`도 같은 뜻입니다.
4. **값이 붙는 옵션은 기본값이 반드시 있습니다.** 이름만 쳐도 뜻이 서야 합니다 — `-speed`=`keep`, `-commands`=`with`, `-answers`=`skip`, `-count`=`others`, `-slot`=모든 수정본.
5. **빼도 동작해야** 옵션입니다. 되돌릴 수 없는 일을 옵션으로 켜지 않습니다(유일한 예외 `-proceed`는 새 동작이 아니라 질문을 건너뛰는 절차입니다).
6. **자리는 명령 뒤 어디든**입니다. 키워드·인자의 **앞이든 뒤든** 같게 읽습니다(`playlist -id base` = `playlist base -id`, `report -copy print 제목` = `report print -copy 제목`). 다만 **자유 글자**(`set`의 값, `import`·`override`의 JSON, `load`·`retitle`의 제목, `prompt`의 제목) 뒤에 오는 `-…`는 글자의 일부로 읽으므로 옵션은 그 앞에 둡니다. 같은 `[ ]` 묶음에서는 하나만 씁니다.

### 키워드의 규칙

1. **그 명령 안에서만** 뜻이 섭니다. 같은 낱말이 명령마다 달라도 됩니다.
2. **낱말 전체가 일치할 때만** 키워드로 읽습니다(자유 텍스트 인자를 지키기 위해).
3. **명령 이름과 같은 낱말을 쓰지 않습니다**(`CmdCatalog.NameClash()`가 검사). 그래서 곡 이름을 바꾸는 명령은 `retitle`이고 `rename`은 `report`의 키워드입니다.
4. **`list`와 `all`은 다릅니다.** `list`는 **나열해 보여 달라**는 뜻이고(출력이 목록: `help` `option` `keyword` `prompt` `report` `checkpoint`), `all`은 **전부를 대상으로**라는 뜻입니다(대상이 전부: `playlist` `id` `title`의 곡, `note`의 노트). **`*`는 `all`을 대신할 수 있지만**(`load *`) **`list`는 대신하지 않습니다.**
5. **상태를 바꾸는 동작**은 키워드입니다(`sync rebuild`, `link follow`, `checkpoint next`).
6. **기본 키워드**: 인자 없이 쳤을 때 하는 일이 키워드 하나와 같으면 그것이 그 명령의 기본 키워드입니다. 명령 표의 `Keywords`에 `*`를 붙여 적고(`"list*, last"`), **도움말은 그것을 글로 적지 않고 `[list]`처럼 대괄호로 보여 줍니다**(사용자 규칙: 말하지 않고 표시). 기본 키워드는 하나뿐이며 그 명령의 키워드여야 합니다.
7. **필수 키워드**: 키워드가 있어야만 무엇을 할지 정해지는 명령(`report <what>`, `install <what>`, `do <action>`)은 **옵션만으로는 실행되지 않습니다** — `report -copy`는 아무것도 하지 않고 받는 낱말을 알려 줍니다. 그래서 그런 명령에는 **자기 키워드와 같은 일을 하는 옵션을 두지 않습니다.** 반대로 기본 키워드가 있는 명령(`help` → `[list]`, `playlist` → `[all]`, `scannow` → `[all]`)은 옵션만 쳐도 기본 키워드로 돌아갑니다. 인자가 키워드가 아닌 명령(`load <title>`, `note <range>`)은 여기에 해당하지 않습니다.
8. **가변 인자**(1.0.0, 사용자 규칙): 여러 개를 한 번에 받는 인자는 **쉼표로 나눕니다** — 띄어 써도 되고 붙여 써도 됩니다(`scannow maps, linker` = `scannow maps,linker`). **가변 인자 뒤에는 다른 인자가 올 수 없어** 사용법의 맨 끝에 `<area>, …`로 적습니다(`scannow [<area>, …]`, `install <what>[, …]`). 빈 자리(`maps,`)와 쉼표 없는 두 낱말(`maps linker`)은 오류이고, `all`·`*`는 이미 전부라서 혼자 씁니다. 지금 가변 인자는 `scannow`·`install`뿐이며, 어울리는 후보는 `docs/future-plans.md`에 적어 두었습니다.

### 중복에 대한 정책

**같은 일을 두 길로 할 수 있는 것은 위반이 아닙니다**(`-og`와 `-origin`, `copy -paste`와 `paste -copy`). 금지는 반대쪽 하나 — **같은 말이 명령에 따라 다른 뜻을 갖는 것**입니다. 하나의 예외는 키워드의 규칙 7입니다: 필수 키워드 명령에서 키워드와 같은 일을 하는 옵션은 "옵션만으로 실행"을 열어 주므로 두지 않습니다.

### 어기면 테스트가 막습니다

`CmdConsoleTests`의 **"Options and keywords keep to their rules"** 묶음이 매 빌드에서 검사합니다: 옵션 이름이 낱말인지 · 이름에 하이픈이 없는지 · 값이 있는 옵션에 기본값이 있는지(그리고 그 기본값이 값 목록 안에 있는지) · 설명이 있는지 · 이름과 별칭이 겹치지 않는지 · 명령이 표에 없는 옵션을 쓰지 않는지 · 키워드가 `-`로 시작하지 않고 설명이 있는지 · 명령과 키워드가 같은 낱말이 아닌지 · **기본 키워드가 하나 이하이고 대괄호로 보이는지** · **필수 키워드 명령에 키워드와 같은 이름의 옵션이 없는지**(그리고 `report -copy`가 실제로 거절되는지) · **설명글이 짧은 별칭을 쓰지 않는지** · **`-force`와 `-proceed`가 한 묶음인지** · **가변 인자가 사용법의 맨 끝인지**. 새 옵션을 `CmdCatalog.Options`에 넣지 않거나 규칙을 어기면 **빌드가 실패합니다.**

## 입력 규칙

- 형식은 `명령 [옵션…] [키워드·인자…] [옵션…]`입니다. **옵션은 키워드·인자의 앞이든 뒤든** 올 수 있습니다. 자유 글자(`set` 값, `import`·`override` JSON, `load`·`retitle`·`prompt` 제목) 뒤의 `-…`만은 글자로 읽습니다. `report`는 제목이 자유 글자지만 낱말 앞뒤의 옵션을 스스로 걸러 냅니다(`report write -copy 제목`).
- 옵션은 **띄어쓰기로만** 구분하고 순서는 상관없습니다(`playlist -hidden -id -copy base`). 도움말은 `[-id | -title] [-hidden | -shown] [-copy]`처럼 적으며, 같은 `[ ]` 안의 옵션은 함께 쓸 수 없습니다.
- **가변 인자**는 쉼표로 나눕니다(띄어쓰기는 자유): `scannow maps, linker`, `install -reinstall app,linker`. 그 뒤에는 옵션만 올 수 있습니다.
- **값이 붙는 옵션**은 `-slot:3` · `-speed:keep` · `-version:1.0.0`처럼 적고 `=`도 같습니다. `-sl3`처럼 `-v1.0.0`도 읽습니다. 이름만 치면 기본값입니다(`-speed`는 `keep`). 예전 표기 `-sl3`·`-sl01`도 그대로 읽습니다. 같은 옵션을 두 번 쓰거나(`-slot:2 -slot:3`) 한 묶음의 두 옵션을 함께 쓰면 오류이며, 오류 문구는 **도움말이 쓰는 긴 이름**으로 알려 줍니다.
- 대소문자는 구분하지 않습니다(곡 id의 글자 포함). 값 자리의 `-12`, `-1`(마지막 노트), `-`(키 이름)처럼 하이픈 뒤가 글자로 시작하지 않는 것은 옵션으로 보지 않습니다.
- **키워드**는 인자 자리에 오는 정해진 단어입니다(아래 표). `<id>`는 8자리 id 또는 `now`, `<cmd>`는 명령 이름·`list`(모든 명령)·`last`(바로 앞 명령), `<string>`은 곡 묶음 키워드입니다. **기본 키워드는 대괄호로 보입니다** — `keyword playlist`의 첫 줄은 `playlist keywords: base, custom, edited, main, now, family, origin, [all]`입니다.
- **Tab 자동완성**: 치고 있는 단어를 완성하고, 다시 누르면 다음 후보, Shift+Tab은 이전 후보입니다. 후보는 **그 명령이 실제로 다루는 것**입니다 — `report`는 첫 칸에 **낱말**을, 그 뒤에는 있는 메모를 부르는 낱말(`write` `print` `done` `undone` `ask` `state` `erase` `rename`)일 때만 **메모 제목**을 내놓고, `prompt`는 `list`와 저장된 프롬프트, `retitle`은 자유 글자라 후보가 없습니다. **모든 명령이 자기 키워드를 첫 인자에 내놓고**(`link `→`start stop tiles…`, `autosave `→`on off`), **`-`를 치면 그 자리가 어디든**(자유 글자 뒤 제외) 아직 쓰지 않은 옵션을 내놓습니다. 빈 줄에서는 `cmd`부터 나옵니다. 설정 경로는 `.`을 칠 때까지 구역을, 그 뒤에는 그 구역의 설정을, `set` 값 자리에서는 고를 수 있는 값을 완성합니다. `load`는 제목의 **어느 부분**을 쳐도 원곡(기본·커스텀) 제목을 통째로 완성하고(`load hu` → `load Raimukun - C†hulhu`), `origin`·`edited`도 완성합니다. 수정본의 `1*제목` 형태는 완성하지 않습니다.
- ↑↓ 이전 명령, Esc 줄 비우기. [Y/n]은 질문 줄 끝에서 답합니다(빈 답은 다시 물음). `-proceed`로 건너뛴 질문은 `[Y/n] Y (-proceed)`로 남습니다. `load`의 번호 목록은 `Pick 1-n:` 뒤에 번호로 답하며 `01`과 `1`은 같습니다. 잘못 친 답은 **한 번만** 다시 묻고 두 번째에는 `Cancelled.`로 끝냅니다.

## 명령 (`cmd` 출력 순서)

옵션 칸은 도움말이 적는 그대로입니다(짧은 별칭은 아래 옵션 표).

| 묶음 | 명령 | 옵션 | 동작 |
| --- | --- | --- | --- |
| Help | `cmd` | [-copy] | 명령 목록 |
| | `help [<cmd>]` | [-copy] | 명령 설명과 그 명령의 **옵션·키워드 목록**(따로 묻지 않아도 나옴). 기본 키워드 `[list]`는 모든 명령 |
| | `option [<cmd>]` | [-keywords] [-copy] | `[list]`면 모든 옵션과 뜻, 명령을 적으면 그 명령의 옵션. `-keywords`는 키워드도 붙임(명령이 없으면 모든 키워드와 쓰는 명령) |
| | `keyword [<cmd>]` | [-options] [-copy] | `[list]`면 모든 키워드와 쓰는 명령, 명령을 적으면 그 명령의 키워드와 뜻. `-options`는 옵션도 붙임. `option -keywords`와 내용이 같고 순서만 다름 |
| | `version` | [-number] [-copy] | 앱 버전·적용 날짜·**이 빌드를 만든 시각**·**최신인지**(`Latest  yes, this is the latest version` / `Latest  no, 1.0.1 is waiting; update takes it`, 1.0.0, 사용자 요청). 최신 여부는 저장소에 물어야 해서 1~2초 진행 막대가 뜨고, 연결이 안 되면 확인하지 못했다고만 적습니다. `-number`는 숫자만 찍고 묻지 않습니다 |
| | `fps` | [-restart] [-copy] | **그리기 상태 진단**: 프레임이 **도착한 간격**(min·중앙값·p95·최대)과 **합성기가 의도한 간격**(Composer), 둘의 지연 차이(Handoff), 앱이 한 프레임에 쓴 시간, 늦은 프레임 수와 **그중 합성기가 아예 주지 않은 수**, GC 횟수, 렌더 티어. 마지막 줄이 원인(그리기 / 합성기 건너뜀 / 늦은 전달 / GC·전원)을 짚음. 표본은 최근 약 2400프레임이고 GC 횟수는 측정 시작 이후 전체이며, 두 구간을 각각 적음. 마지막 줄에 **스케줄링 상태**(멀티미디어 클래스 등록 여부)와 **GC 지연 모드**도 적음. `-restart`는 출력 뒤 측정을 새로 시작 |
| | `link [<what>]` | [-copy] | **게임 동기화 링크**(`docs/game-sync-plan.md`). 인자 없이 치면 상태: 도착 간격(min·중앙값·p95·최대), 지연의 흔들림(Delay), 곡 위치가 실제 시간 대비 얼마나 빠르게 가는지, 모드 버전, 게임의 상태·일시정지·타일·실제 배속·게임 보정값·레벨 경로, 상태 변화 기록. `start` 파이프 열기 · `stop` 닫기 · `again` 측정 새로 시작 · **`tiles`** 게임이 계산한 타일 시각과 불러온 곡의 hit 시각 대조(둘 다 일정표라 같게 읽었으면 차이가 모든 타일에서 같음, 허용 1ms) · **`follow`** 게임 곡 위치에 맞춰 재생 지점만 움직임(노트·판정 불변, 타일이 어긋나면 거절, 차이 60ms 초과면 즉시 이동·이하는 읽기마다 15%씩 당김) · `unfollow` 되돌리기 · **`auto`** 불러온 곡과 게임의 곡이 같으면 `follow`를 사람이 치지 않아도 스스로 한 번 시도(거절 이유는 `link`의 `Auto` 줄에 남김, 곡을 바꿔 주지는 않음) · `manual` 그만두기. 게임에 아무것도 보내지 않고 받은 값은 저장하지 않음 |
| | `install <what>[, …]` | [-reinstall] [-proceed] | **게임 링커 설치**(0.9.2)와 **다시 설치**(1.0.0). `install linker`는 읽기 전용 링커 모드를 `<게임>\Mods\AdofaiVisualizerSync\`에 넣습니다 — **이 앱이 자기 폴더 밖에 쓰는 곳**(AGENTS.md 안전 규칙 2)이므로 어느 폴더에 쓸지 알리고 [Y/n]을 물으며, 모드의 두 파일만 쓰고 다른 모드는 건드리지 않습니다. Unity Mod Manager가 없으면 아무것도 하지 않고, 같은 버전이 이미 설치되었으면 그대로 둡니다. **`-reinstall`**은 `scannow`가 문제를 찾은 부분을 이 버전대로 다시 씁니다: `source`(저장소 파일을 이 버전으로 되돌림 — **바뀔 파일 목록을 먼저 보여 주고 항상 묻습니다**, `-proceed` 불가) · `app`(Release를 다시 빌드) · `linker`(이 버전으로 모드를 빌드해 다시 씀, 앱은 그대로) · `maps`(= `sync rebuild`) · `all`·`*`(이 버전 완전 재설치: source·app·설치된 링커). `source`·`app`이 들어가면 앱을 닫았다가 다시 엽니다. **`edits`·`data`는 사용자의 것이라 재설치하지 않고** 무엇을 쓰면 되는지만 알려 줍니다. 링커가 설치되지 않았으면 `install linker`부터 하라고 합니다. 모두 저장소의 `update.ps1 -Reinstall`이 합니다 |
| | `scannow [<area>, …]` | [-copy] | **무결성 검사**(1.0.0, 사용자 요청). 고치지 않고 **결과만 출력**합니다. 기본 `[all]`이며 영역은 `app`(이 빌드의 버전·빌드 뒤에 바뀐 소스·언어와 테마 파일) · `source`(저장소 파일이 이 버전의 커밋과 같은지: changed·missing·extra) · `linker`(설치된 모드의 버전·파일·빌드본과 같은지) · `maps`(세 폴더의 맵 사본과 곡 목록: 목록에 없는 사본, 파일이 사라진 곡, 동기화 뒤 바뀐 파일, 옛 판독기로 읽은 곡, 맵 폴더) · `edits`(슬롯마다 원본 사본·edit.json·레인 범위·원본 해시) · `data`(settings.json·songs.json·song-ids.json의 겹친 id·song-marks.json·메모·저장 폴더). 영역마다 `ok` 또는 `n problems`와 문제 줄(20줄까지)을 찍고, 맨 끝에 **`Run install -reinstall maps, linker to put them right.`**처럼 고칠 명령을 권합니다. **링커가 설치되지 않았으면** 전체 검사에서는 `not installed` 한 줄이고, `scannow linker`로 콕 집으면 `install linker`부터 하라고 하고 멈춥니다 |
| Songs | `song` | [-id \| -title] [-copy] | 현재 곡 정보(`info now`와 같음). `-id`·`-title`은 `id`·`title`과 같은 출력. 0.9.1에서 `now`에서 이름을 바꿨습니다(키워드 `now`와 겹쳐서) |
| | `info <id>` | [-title] [-copy] | id·제목·작곡가·제작자·종류·길이·노트 수(`notes`, 실제로 누르는 노트만)·롱노트 수(`longnotes`)·자동 타일 수(`autonotes`, 0이어도 표시)·난이도·체크포인트(곡 시작은 세지 않음)·슬롯(원곡은 `origin`)·숨김·상태·경로 |
| | `id [<string>]` | [-title] [-copy] | 곡 id, 기본 `[now]`. 수정본은 `2k3j9a0q (slot 2)`. `-title`은 제목도(`id 제목`) |
| | `title [<string>]` | [-id] [-copy] | 곡 제목, 기본 `[now]`. 수정본은 `Plum - R (slot 2)`. `-id`는 id도(`제목 id`). `title -id`와 `id -title`은 순서만 다름 |
| | `load <title>` | [-id \| -origin \| -slot[:\<n\>]] [-speed[:keep\|reset]] [-hidden] [-force] | 곡 불러오기(아래 절). `*`는 전체, `.`은 불러온 곡. 가려진 곡은 빠지며 `-hidden`으로 포함 |
| | `playlist [<string>]` | [-id \| -title] [-hidden \| -shown] [-copy] | 곡 목록(`id 종류 제목`, 숨긴 곡은 `[hidden]`), 기본 `[all]`. **`playlist main`은 메인 화면 목록 그대로**(예전 `playlist`). `-id`·`-title`은 `id`·`title`과 같은 출력. 0.9.2에서 명령 `list`를 여기에 합쳤습니다 |
| | `checkpoint [<number>]` | [-copy] | 불러온 곡을 그 체크포인트부터 준비(메인 화면 목록과 같은 번호). 키워드 `first` `next` `previous` `last`, 인자가 없으면 지금 서 있는 자리, **`list`**는 목록(쓰는 자리에 `>`). 곡이 없거나 체크포인트가 없는 맵이면 이유만 알립니다 |
| | `locate <tile>` | 없음 | 불러온 곡을 그 타일부터 준비(구간 칸과 같은 규칙: 그 타일 이후 첫 노트부터). 자연수만 받고, 그 곡에 없는 타일·중간 회전 타일·롱노트 도중이면 거절합니다. 처음으로 돌아가려면 `checkpoint first` |
| | `hide <id>` · `show <id>` | 없음 | 곡 숨기기·보이기(즉시 저장) |
| Editing | `edit [<id>]` | [-proceed] | 그 곡의 수정 창(불러오지 않은 곡도 가능). 기본 `[now]`(불러온 곡) |
| | `note [<id>] <range>` | [-proceed] [-json] [-copy] | 노트 목록 `n<번호> <레인> <다음까지 ms>`, 범위 `all` `*` `a..b` `a..` `..b` `n`, **`-1`은 마지막 노트**. `-json`은 `{ "notes": [{ "n", "tile", "ms", "chord" }] }`로 출력하며 `override`가 그대로 읽습니다. **곡을 고를 수 있고 기본은 `[now]`**(1.0.0, 사용자 요청): `now`는 화면에 있는 것 — **수정 창이 열려 있으면 수정 중인 레인을 저장 여부와 상관없이 그대로**, 아니면 불러온 곡을 재생되는 그대로(수정본은 그 레인) 냅니다. id를 주면 그 곡을 불러오지 않고 재생될 모양으로 냅니다. 수정본은 지금 화면에 없는 레인이어도 있는 그대로 찍습니다. **수정 창이 열려 있어도 동작합니다**(0.9.2까지는 거절) |
| | `override <json>` | [-proceed] | **수정 창이 열려 있을 때만.** `note -json` 형태의 JSON으로 레인을 통째로 바꿈. 번호와 레인만 반영하고 `ms`는 대조용이며, 한 줄이라도 어긋나면 **아무것도 바꾸지 않고** 첫 문제를 알림 |
| | `retitle <title>` | [-proceed] | 불러온 곡의 표시 이름을 바꿈. 바꾸기 전·후를 보여 주고 [Y/n]. 수정본은 `n*`가 강제로 붙고, 이름은 **원곡에 저장**되어 그 곡의 모든 수정본이 함께 바뀜. 예전 이름은 `rename`이었습니다(`report rename`과 겹쳐서 0.9.2에 바꿈) |
| Song files | `sync [rebuild]` | [-proceed] | 맵 폴더를 커스텀 맵으로 들여오고 세 폴더를 다시 읽음(설정의 동기화 버튼과 같음). **묻지 않습니다.** 키워드 `rebuild`는 커스텀 맵을 **새로 만들어** 맵 폴더에 없는 곡을 목록에서 내리며, 이때만 [Y/n]을 묻습니다(0.9.2에서 옵션 `-re`를 키워드로 바꿨습니다). `sync -proceed`는 `sync`와 같습니다 |
| | `remove <id>` | [-proceed] | **수정본만** 제거. `restore`로 복구 가능 |
| | `restore` | [-proceed] | 마지막으로 제거한 수정본 한 곡만 복구(두 번째부터는 없음) |
| | `delete <id>` | [-proceed] | 어떤 곡이든 영구 삭제. 커스텀 맵은 다음 동기화 때 다시 복사됨 |
| Settings | `get <setting>` | 없음 | 설정 값(토글은 항상 `true`/`false`) |
| | `set <setting> <value>` | 없음 | 바꾸고 즉시 적용·저장. 토글은 `true/false` 외에 `on/off`, `1/0`, `able/disable`(`enable`)도 받음 |
| | `speed [<percent>]` | [-rate] | 배속. `set timing.speed`와 같고, 값 없이 치면 지금 배속. **`-rate`를 주면 배속이 아니라 노트 이동 시간**(`set display.travel`, ms라 작을수록 빠름)을 보거나 바꿉니다 |
| | `autosave [on \| off]` | 없음 | 설정 자동 저장(`general.autosave`). 값 없이 치면 현재 상태 — **설정 바로가기 명령은 모두 값이 없으면 현재 값을 보여 줍니다** |
| | `property [<section>[.<setting>]]` | [-copy] | 구역 목록 / 구역의 설정과 현재 값 / 설정이 받는 값 |
| | `export` | [-copy] | 설정 JSON 출력(설정 내보내기와 같음, 두 폴더 경로 제외) |
| | `import <json>` | [-proceed] | 붙여넣은 JSON에 적힌 설정만 바꿈. 바뀔 항목을 보여 주고 [Y/n] |
| | `rollback` | [-proceed] | 마지막 설정 변경을 되돌림. 무엇이 무엇으로 돌아가는지 보여 주고 [Y/n] |
| | `reset` | [-proceed] | 두 폴더 경로를 뺀 모든 설정을 기본값으로 |
| | `do <action>` | 없음 | 설정 창의 동작(키워드 표). 필수 키워드입니다 |
| Console | `copy [<count>]` | [-commands[:with\|only]] [-answers[:with\|skip]] [-count[:all\|others]] [-paste] | 화면의 **메시지**를 클립보드로(기본 1개). 숫자는 위로 몇 개까지, `*`는 전체, `-1`은 가장 처음 메시지, `a..b`는 구간입니다(`note`와 같은 표기). `-commands`는 친 명령도 `Visualizer>` 그대로 함께(`:only`면 친 명령만), `-answers`는 [Y/n] 답한 줄 빼기, `-count`는 세는 동안 `copy`와 `-copy`를 쓴 메시지 건너뛰기, `-paste`는 화면에도 다시 보여 줌. 범위를 넘으면 끝까지로 보고, 복사할 것이 없을 때만 알립니다 |
| | `paste [<count>]` | [-commands[:with\|only]] [-answers[:with\|skip]] [-count[:all\|others]] [-copy] | 그 메시지를 **화면에 다시** 보여 줌(인자는 `copy`와 같음). `-copy`를 주면 복사도 함께. 프롬프트의 빈칸에서는 `/paste`로 그 출력을 값으로 넣습니다 |
| | `prompt [<title>]` | [-copy] | 저장해 둔 **프롬프트**(기본 `[list]`는 제목 목록). 빈칸이 있으면 하나씩 묻고(아래 절) 마지막에 보여 준 뒤 복사합니다. `note-lanes`는 `note -json` 결과를 언어 모델에 고쳐 달라고 할 때 쓰는 프롬프트입니다. 아무 곳에도 보내지 않고 실행하지도 않습니다 |
| | `report <what> [<title>]` | [-force] [-proceed] [-copy] | 에이전트에게 남기는 메모 **편집기**(아래 절). **낱말이 필수**입니다(0.9.2, 사용자 규칙) — `report`나 `report -copy`만 치면 받는 낱말을 알려 주고 아무것도 하지 않습니다. `create <title>` 새로 만들며 열기(이미 있으면 거절, `-force`면 [Y/n] 뒤 비운 채 열고 저장해야 바뀜) · `write <title>` 있는 메모 열기(없으면 거절) · `list [<word>]` 목록(그 글자가 든 것만) · `print` 열지 않고 찍기 · `done`/`undone` 반영 표시 · `ask <title> > <reason>` 못 풀었음과 이유 · `state` 서 있는 자리만 · `erase` 지우기([Y/n]) · `sweep` 반영된 메모 전부 지우기([Y/n]) · `rename <old> > <new>` 이름 바꾸기([Y/n]) · `colon` 편집기 `:` 명령. 제목이 낱말과 같으면 `report write "create"`처럼 따옴표 |
| | `update` | [-force \| -proceed] [-version[:latest\|<version>]] | **최신 버전 받기**(1.0.0 개편, 사용자 요청). 먼저 저장소에 물어 **최신이면 `This copy is already the latest version (1.0.0).`로 끝내고**, 새 버전이 있으면 `Update to 1.0.1? The app closes and opens again on the new version.` [Y/n](`-proceed`로 건너뜀) 뒤 **앱을 닫고 그 버전을 빌드한 다음 다시 엽니다**. **링커가 설치되어 있으면 함께 갱신**합니다(질문에 적음). 이 복사본에 자기 수정이나 올리지 않은 커밋이 있으면 거절하고 `update -force`를 알려 줍니다. **`-force`**는 그런 것을 버리고 가며, **`-version:1.0.0`**(짧게 `-v:1.0.0`·`-v1.0.0`, `-version`만이면 `latest`)으로 **특정 버전으로 강제 이동**합니다 — 이때는 **위험은 사용자 책임**이라는 말과 대상 버전·재시작을 적고 **언제나 [Y/n]**을 묻습니다(`-proceed` 불가). 최신이 아닌 버전은 `-force` 없이는 거절하고, 저장소에 없는 버전은 `There is no version 1.0.7 on the repository.`라고만 답합니다(버전 목록은 보여 주지 않음, 사용자 규칙). 버전은 `Directory.Build.props`의 `<Version>`을 바꾼 커밋으로 찾으므로 태그가 필요 없고, 내려간 뒤 그냥 `update`하면 다시 최신으로 올라옵니다. 업데이트 뒤 첫 실행에는 **무엇이 바뀌었는지 보여 주는 창**이 마지막 곡 질문보다 먼저 뜹니다. 곡·설정·메모는 저장소 밖이라 건드리지 않습니다. 저장소의 `update.ps1`을 그대로 실행합니다 |
| | `clear` | 없음 | 화면 지우기 |
| | `exit` | [-keep] | 화면을 지우고 닫음. `-keep`은 화면 유지(X로 닫아도 유지) |
| | `date` | [-copy] | 이 컴퓨터의 날짜와 시각 |
| | `whoami` | 없음 | VISUALIZER 글자 그림 |

`whoami`는 VISUALIZER 글자 그림(6줄, 72칸)을 출력합니다. 콘솔 글꼴(Cascadia Mono, 없으면 Consolas)이 그림의 모든 문자를 글자와 같은 폭으로 그리고, 콘솔 창의 최소 폭(640)은 그림이 줄바꿈되지 않게 정했습니다. 창의 기본 크기는 990×624입니다(0.5.8). 숨긴 명령은 없습니다.

## 옵션

| 옵션 | 짧은 별칭 | 뜻 | 명령 |
| --- | --- | --- | --- |
| `-copy` | `-c` | 출력하고 클립보드에도 복사 | cmd, help, option, keyword, version, fps, link, song, info, id, title, playlist, checkpoint, note, property, export, paste, prompt, report, date, scannow |
| `-proceed` | `-p` | [Y/n]에 모두 Y(`[Y/n] Y (-proceed)`로 남김). **`-force`와 함께 쓸 수 없고**, `install -reinstall source`·`*`도 대신 답하지 못합니다 | install, edit, note, override, retitle, sync, remove, restore, delete, import, rollback, reset, report, update |
| `-id` | | id만(song·playlist) / id도(title) / id로 찾기(load, 8자 전부) | song, title, load, playlist |
| `-title` | `-tl` | 제목만(song·info·playlist) / 제목도(id) | song, info, id, playlist |
| `-hidden` · `-shown` | `-h` · `-s` | 숨긴 곡만 · 보이는 곡만(playlist) / 가려진 곡도 함께(load) | load, playlist |
| `-origin` | `-og` | 원곡만, 하나면 바로 불러옴 | load |
| `-slot` · `-slot:<n>` | `-sl` · `-sl3` | 수정본만(하나면 바로) · 그 슬롯의 수정본. **이름만 쓰면 전체** | load |
| `-speed:keep` · `-speed:reset` | `-sm` · `-sr` | 배속 유지 · 100%로 초기화. **`-speed`만 쓰면 `keep`** | load |
| `-force` | | 거절될 일을 묻고 나서 그대로 함: 지원하지 않는 곡을 검증된 다른 형식으로 읽어 불러옴(load) · 이미 있는 제목 위에 새 메모(report create) · 자기 수정을 버리고, 또는 최신이 아닌 버전으로 갱신(update). **언제나 [Y/n]**, `-proceed`와 함께 쓰면 오류 | load, report, update |
| `-version:<version>` | `-v` | 갈 버전: `latest`(이름만 쓰면 이것) 또는 `1.0.0`처럼 | update |
| `-reinstall` | | 있어도 이 버전대로 다시 씀 | install |
| `-rate` | `-ar` | 배속이 아니라 **노트 이동 시간**(ms) | speed |
| `-keywords` · `-options` | `-k` · `-o` | 키워드도 · 옵션도 | option · keyword |
| `-number` | `-n` | 숫자만 | version |
| `-restart` | `-r` | 출력 뒤 측정을 새로 시작 | fps |
| `-keep` | `-m` | 화면 유지 | exit |
| `-json` | | JSON으로 출력 | note |
| `-paste` | `-pa` | 복사하면서 화면에도 다시 보여 줌(`paste -copy`와 같음) | copy |
| `-commands:with` · `-commands:only` | `-vs` · `-vo` | 친 명령도 함께(`Visualizer>` 포함) · 친 명령만. **이름만 쓰면 `with`** | copy, paste |
| `-answers:skip` · `-count:others` | `-ep` · `-en` | [Y/n] 답한 줄 빼기 · 세는 동안 `copy`·`paste` 자신은 빼기. **이름만 쓰면 각각 `skip`·`others`** | copy, paste |

## 키워드

대괄호는 **기본 키워드**(인자 없이 쳤을 때의 뜻)입니다. 도움말에도 이렇게 보입니다. **필수**는 키워드 없이는 실행되지 않는 명령입니다(옵션만으로도 안 됨).

| 명령 | 키워드 |
| --- | --- |
| `help` `option` `keyword` | `[list]`(모든 명령), `last`(바로 앞 명령) |
| `info` `hide` `show` `remove` `delete` | `now`(현재 곡) |
| `edit` | `[now]`(현재 곡) |
| `playlist` `id` `title` | `base` `custom` `edited`(폴더별), `main`(메인 화면 목록 그대로: 곡 그룹·숨김·정렬·표시 이름, 검색창 글자는 무시), `now`(현재 곡 하나), `family`(현재 곡의 원곡과 모든 수정본, 원곡 먼저·슬롯순), `origin`(현재 곡의 원곡, 이미 원곡이면 그 곡), `all`(모든 곡). 기본은 playlist `[all]`, id·title `[now]` |
| `load` | `origin`, `edited` |
| `note` | `[now]`(화면에 있는 것: 수정 창이 열려 있으면 수정 중인 레인, 아니면 불러온 곡), `all`(모든 노트) |
| `prompt` | `[list]`(제목 목록) |
| `report` | **필수**: `list`(메모 목록), `create`(새로 만들며 열기), `write`(있는 것 열기), `print`(찍기), `done` `undone`(반영 표시), `ask`(못 풀었음과 이유), `state`(서 있는 자리), `erase`(지우기), `sweep`(반영된 것 모두 지우기), `rename`(이름 바꾸기), `colon`(편집기 `:` 명령) |
| `install` | **필수**, 쉼표로 여러 개: `linker`(읽기 전용 게임 링커 모드), `app`, `source`, `maps`, `edits`, `data`, `all`(`-reinstall`과 함께 이 버전 완전 재설치) |
| `scannow` | 쉼표로 여러 개: `[all]`, `app`, `source`, `linker`, `maps`, `edits`, `data`(뜻은 위 명령 표) |
| `link` | `start` `stop` `tiles` `follow` `unfollow` `again` `auto` `manual`(위 명령 표) |
| `sync` | `rebuild`(커스텀 맵을 새로 만들어 맵 폴더에 없는 곡을 내림) |
| `checkpoint` | `list`(목록), `first`(곡 처음), `next` `previous`(목록을 한 칸씩, 양 끝에서 순환), `last`(마지막 체크포인트) |
| `autosave` | `on`, `off` |
| `do` | **필수**: `clear-previous-checkpoint-key`, `clear-next-checkpoint-key`, `clear-settings-key`, `map-folder`, `data-folder`, `view-playlist-details`(곡 리스트 상세 창, 0.6.0까지의 `playlist`) |

**`keyword <키워드>`**(0.9.1): 명령 이름 대신 키워드를 적으면 그 키워드의 뜻과 그것을 받는 명령을 보여 줍니다. 명령 이름과 키워드가 같은 단어는 없습니다(그래서 0.9.1에서 명령 `now`가 `song`이 되었습니다).

0.9.1에서 `current` → **`main`**, `ones` → **`family`**로 바꿨습니다(`now`와 헷갈려서).

**1.0.0에서 바뀐 것**: `note`가 `note [<id>] <range>`가 되었고(기본 `now`, 수정본은 자기 레인으로 — 0.9.2까지 수정본도 원곡 레인으로 찍히던 결함을 고침), 수정 중에도 동작합니다. 새 명령 `scannow`, `install`의 `-reinstall`과 가변 인자, `update`의 `-force`·`-version`, `version`의 최신 여부. `-force`는 언제나 묻고 `-proceed`와 함께 쓸 수 없습니다.

**0.9.2에서 바뀐 것**(사용자 규칙, **예전 입력이 더 이상 통하지 않는 첫 변경**): 명령 `list`는 `playlist`로 합쳤습니다(예전 `playlist` = 지금 `playlist main`). 곡 이름을 바꾸는 명령 `rename`은 **`retitle`**이 되었습니다(`report rename`과 겹쳐서). `report`는 **낱말이 필수**가 되어 `report <제목>`과 동작 옵션(`-list` `-print` `-remove` `-rename` `-clear` `-done` `-undone` `-ask` `-state` `-colon`)이 없어졌고 같은 일을 낱말로 합니다(`-remove` → `erase`, `-clear` → `sweep`). `checkpoint -list`는 `checkpoint list`, `help`·`option`·`keyword`의 `all`은 `list`가 되었습니다. 옵션은 키워드 뒤에 와도 됩니다.

0.6.1에서 바뀐 것: `list now`·`id now`는 예전에 원곡과 수정본 전부를 냈으나 이제 **현재 곡 하나**이고, 그 동작은 `ones`(지금의 `family`)가 맡습니다. `do`의 `clear-previous-key`·`clear-next-key`는 체크포인트 키라는 것을 이름에 넣었습니다.

## load (0.6.1)

- 제목은 **어느 부분이든**(중간부터도) 맞으면 됩니다. 대상은 원곡(기본·커스텀)이며, 맞는 원곡마다 그 **수정본을 바로 아래에** 붙여 **한 목록**으로 보여 주고 번호로 고릅니다. 원곡 하나만 맞고 수정본이 없으면 **바로** 불러옵니다.
- 수정본은 목록 제목 그대로(`1*Plum - R`) 정확히 치면 바로 불러옵니다(자동완성에는 없음).
- `-origin` 원곡만, `-slot` 제목이 맞는 수정본만(원곡이 사라진 수정본 포함), `-slot:<n>` 그 슬롯의 수정본만. 하나면 바로, 여럿이면 목록.
- `-id <id>`: 8자 id가 **정확히** 같아야 합니다(대소문자 무관). 두 번째 단어는 받지 않습니다.
- **후순위 id 검색**: 제목에 맞는 곡이 없고 친 글자가 id 모양(8자, 첫 자리 0–2)이면 그 id로 찾습니다. 그래도 없으면 둘 다 없다고 알립니다.
- 키워드: `load origin`은 현재 수정본의 원곡을 불러옵니다(현재 곡이 원곡이면 거부하고, 수정본이 있을 때만 `load edited`를 안내). `load edited`는 현재 원곡의 수정본을 목록으로(하나면 바로), `-slot:<n>`을 붙이면 그 슬롯을 바로 불러옵니다(현재 곡이 수정본이면 거부하고 `load origin` 안내).
- 목록이 **100개 이상**이면 출력하지 않고 제목을 더 적어 달라고 합니다(01–99까지 표시).
- 배속: 고급 설정의 **불러와도 배속 유지**가 꺼져 있으면(기본) 불러올 때 배속을 100%로 되돌리고 `timing.speed: 80 → 100`을 알립니다. `-speed`(`:keep`)는 유지, `-speed:reset`은 초기화이며 설정과 무관하게 따릅니다. 되돌린 배속은 `rollback`으로 살립니다.
- 불러오기 전에 설정 창을 닫습니다(아래 규칙). 메인 화면 목록에 있는 곡이면 검색창에도 그 곡이 골라집니다.
- `-force`(0.6.8, 사용자 규칙)
  - **Unsupported 곡에만** 동작하며 위험하므로 **항상 [Y/n]을 묻습니다**(`load`에는 `-proceed`가 없습니다).
  - 곡의 형식 version보다 낮은 검증 형식을 가까운 것부터 읽어 보고, 없으면 높은 것을 읽어 봅니다(5→4→3→2→8…, 1→2, 20→19). 검증되지 않은 번호는 건너뜁니다.
  - 읽히면 캐시에 그 형식을 남기고 불러오며, `Forced: read as format version N.`을 출력합니다. 곡 목록에는 주황 `forced`가 붙고, 같은 파일인 동안은 동기화 뒤에도 유지됩니다.
  - 모든 형식이 실패하면(형식이 아닌 다른 이유로 막힌 곡) 캐시를 그대로 두고, 이유는 메인 화면에 나옵니다.
  - 다른 곡에 `-force`를 붙이면 `-force is only for unsupported songs…`라고 알리고 그냥 불러옵니다.
  - 근거와 한계: `docs/debug-logs/unsupported-maps.md`.

예시 목록 `abcd (12345678)`, `Raimukun - C†hulhu (12333333)`, `2hello`, `2hi`, `22222222 (18569403)`, 그 수정본 `1*22222222 (28534333)`, 원곡이 없는 수정본 `1*2bye (22222222)`:
`load 2` → 22222222·1*22222222·2hello·2hi 목록 / `load -slot:1 2` → 1*22222222·1*2bye 목록 / `load hu`+Tab → Raimukun - C†hulhu 바로 / `load 12345678` → 제목에 없으니 id로 abcd / `load -id 22222222` → 2bye / `load -id 2` → 오류. 이 사례와 경계 상황은 `CmdConsoleTests`의 load 그룹이 고정합니다.

## 동작 규칙

- **설정 먼저 닫기**: `edit` `set` `speed` `import` `rollback` `reset` `do` `hide` `show` `load`는 설정이 열려 있으면 닫고 실행합니다(0.8.9부터 설정은 창이 아니라 메인 창 안에서 열리는 패널입니다). 적용하지 않은 변경이 있으면 `[Y/n]`으로 묻고(Y 적용, n 버림) `-proceed`는 Y입니다. 읽기만 하는 명령(`get`, `property`, `playlist` 등)은 설정을 건드리지 않습니다. (0.9.1부터 수정 창은 설정이 아니라 **노트 영역의 수정 버튼**에서 열리므로 "적용하고 수정" 확인은 없습니다.)
- **수정 창이 열려 있을 때**: 설정·수정 창이 열려 있어도 콘솔을 쓸 수 있습니다. 다만 수정 창이 열려 있으면 `load` `set` `speed` `autosave` `import` `reset` `rollback` `do` `sync` `remove` `restore` `delete` `retitle` `checkpoint` `locate`는 실행하지 않고 "The note editor is open, so <명령> cannot run. Save or close the editor first."를 출력합니다. 읽기만 하는 명령은 그대로 동작하고, `edit`은 열려 있는 창을 앞으로 가져옵니다(다른 곡이면 거절). **`note`는 수정 중에도 동작하며 수정 중인 레인을 그대로 냅니다**(1.0.0).
- **rollback**: 적용된 모든 설정 변경을 앱을 끌 때까지 기록합니다(설정 창 적용, `set`·`import`·`reset`·`do`, 메인 화면 자동 진행 토글, 여분 레인·최대 타일 수 확장에 동의한 강제 변경, 곡을 불러올 때의 배속 초기화). 강제 변경과 배속 초기화는 그 앞의 변경과 따로 한 단계로 남습니다. `rollback`은 가장 최근 단계에서 **바뀐 항목만** 이전 값으로 돌리고, 다시 치면 한 단계 더 돌아갑니다. 시작할 때의 설정 복원과 `rollback` 자신은 기록하지 않습니다. 강제 변경을 되돌려 곡이 배치되지 않으면 그대로 두고 같은 제안을 다시 묻지 않습니다.
- **restore 보관**: `remove`는 수정본의 슬롯 폴더 전체를 `<저장소>/maps/removed/`로 옮기고 이름을 기록합니다. 다음 `remove`가 이전 보관본을 지우므로 한 곡만 남습니다. 복구는 원래 슬롯이 비어 있으면 그 슬롯, 아니면 첫 빈 슬롯입니다. 앱을 다시 켜도 복구할 수 있습니다.
- **delete**: 수정본은 슬롯 폴더째(`edit.json` 포함) 지웁니다. 불러온 곡을 지우면 화면에서도 내립니다.
- **import/export**: 설정 창의 불러오기·내보내기와 같은 규칙입니다. 일부만 적어도 되고, 두 폴더 경로는 이 PC 값을 유지합니다.
- **곡 id**: 첫 자리는 폴더(0 기본, 1 커스텀, 2 수정본), 나머지 7자리는 소문자와 숫자이며 글자가 최소 하나 들어갑니다(0.6.1, 이전은 숫자 8자리). 예전 형식의 id는 형식 검사에 걸려 다음 실행·동기화 때 모두 새로 발급됩니다(`song-ids.json`).

## prompt의 빈칸 채우기 (0.9.1)

프롬프트에 `{request}`처럼 빈칸이 있으면 `prompt <title>`이 **하나씩 묻습니다.** 프롬프트 줄은 들여써서 `    prompt:<제목> (1/2)>`가 되고(콘솔의 `Visualizer>`와 한눈에 구분되게), 친 줄은 명령이 아니라 **그 빈칸의 값**입니다.

| 입력 | 뜻 |
| --- | --- |
| 아무 글자 | 그대로 값이 됩니다(`/string`이 생략된 형태) |
| `/string <글자>` | 뒤의 내용을 글자 그대로. `/`로 시작하는 값을 넣을 때 씁니다 |
| `/<명령>` | 그 명령을 실행해 **출력을 값으로** 넣습니다. `/paste`, `/note -json 1..64`처럼 씁니다 |
| 빈 줄(Enter) | 답으로 치지 않고 **같은 빈칸을 다시** 묻습니다 |
| `/skip` | 그 빈칸을 비워 둡니다(아래 표시가 남습니다) |
| `/back` · `/again` | 이전 빈칸으로 · 처음부터 다시 |
| `/cancel` | 취소하고 나갑니다 |

- **명령은 찍고 끝나는 것만** 받습니다(`paste` `note` `id` `title` `info` `song` `playlist` `get` `property` `export` `version` `date` `fps` `link` `cmd` `help` `option` `keyword` `whoami`). `load`처럼 질문을 띄우거나 창을 여는 명령은 거절하고 다시 묻습니다 — 질문 안에서 질문이 열리면 어느 쪽 답인지 알 수 없기 때문입니다.
- 비워 둔 빈칸에는 `>>> WRITE THE REQUEST HERE. If this line is still here, ask for it instead of guessing. <<<`가 남습니다. 사람에게는 "여기 쓰세요", 모델에게는 "비었으면 묻고 멈춰라"로 읽히게 적었습니다.
- 다 채우면 **줄인 미리보기**(긴 값은 `… n more lines …`)를 보여 주고 `[Y/n/detail]`로 묻습니다. `detail`(`d`)은 전부 출력한 뒤 다시 `[Y/n]`으로 묻고, `Y`면 클립보드로 복사합니다. `n`과 `/cancel`은 취소하되 **채워 둔 내용을 한 번 찍어** 길게 친 글이 사라지지 않게 합니다.
- JSON을 직접 Ctrl+V로 붙여 넣어도 됩니다(콘솔이 줄바꿈을 공백으로 바꾸며, JSON은 뜻이 그대로입니다). 다만 화면이 밀리고 클립보드를 한 번 쓰므로 `/paste`·`/note -json` 쪽이 편합니다.

## report (0.9.1)

다음 버전에 반영할 내용을 적어 두는 콘솔 안의 작은 편집기입니다. 메모는 앱 저장소의 `reports/<제목>.txt`에만 쓰고 그 밖으로 나가지 않습니다.

- **낱말이 필수입니다**(0.9.2, 사용자 규칙): `report create <제목>`은 새로 만들며 열고(있으면 거절, `-force`면 묻고 나서 빈 채로), `report write <제목>`은 있는 메모를 엽니다(없으면 거절). `report`만 치거나 옵션만 주면 받는 낱말을 알려 주고 아무것도 하지 않습니다. 제목은 글자·숫자·공백·`-`·`_`만 쓸 수 있고 겹칠 수 없습니다.
- 열리면 **콘솔 창 안에 편집기 화면**이 뜹니다(창을 새로 띄우지 않고, 기록은 가려질 뿐입니다). 맨 아래 상태줄에 제목·줄 수·커서 위치가 있고, 저장하지 않은 변경은 `[+]`로 표시됩니다.
  - **`i`** 쓰기 모드, **Esc** 보기 모드. 쓰기 모드에서 **Enter는 줄바꿈**이고 방향키·마우스로 아무 줄이나 고칠 수 있습니다. Tab은 공백 네 칸입니다.
  - 보기 모드에서 **`:`**를 누르면 상태줄이 명령줄이 됩니다: `:w`(`:write`) 저장 · `:q`(`:quit`) 나가기(저장하지 않은 변경이 있으면 상태줄에서 [Y/n]) · `:wq`(`:x`) 저장하고 나가기 · `:copy` 고른 글(없으면 메모 전체) 복사 · `:d`(`:delete`) 제거(묻고 나서). **`:q!`는 없앴습니다**(0.9.2).
  - 한글 입력이 됩니다(0.9.2에서 고쳤습니다: 보기 모드의 읽기 전용 상태 때문에 IME가 꺼진 채였습니다). **Ctrl+T**는 편집 중에도 콘솔을 최소화합니다.
  - 상태줄 오른쪽에 **`i write  :w save  :q leave  :d delete`**가 늘 떠 있습니다(0.9.2, 사용자 규칙). 콘솔 줄은 `제목: n lines`만 적고 키를 따라 적지 않습니다.
  - 콘솔 창을 닫아도 **쓰던 메모는 그대로** 있어, 다시 열면 그 상태로 돌아옵니다. 앱을 닫을 때만 저장하지 않은 메모를 경고합니다.
  - `[Y/n]` 질문은 **글자를 치고 Enter**를 눌러야 적용됩니다(콘솔의 다른 질문과 같은 방식). 한 글자만 눌러 바로 움직이지 않습니다.
- **`report list`**: 메모마다 `제목 · 작성 버전 · open/done · 줄 수`를 보여 줍니다. 뒤에 글자를 붙이면 제목이나 **본문**에 그 글자가 든 메모만 남습니다.
- **메모가 서 있는 자리는 셋**(0.9.2): **open**(기다리는 중) · **done**(반영됨, 그 버전과 함께) · **asked**(봤지만 해결하지 못해 답을 기다리는 중 — **이유를 함께 적습니다**). 이유는 **설정의 언어**로 적습니다(사용자가 읽는 글이라서. 콘솔의 다른 문구는 영어 고정이지만 이것은 곡 이름처럼 **데이터**입니다).
- **표시는 메모 옆**(`reports/done.mark`)에 있어 본문에 타이핑해 넣거나 지울 수 없습니다. 보는 곳은 셋 — `report list` 목록(맨 끝 줄에 `n of m still open`, asked는 이유가 아랫줄에), 편집기 **상태줄**(`"제목" done 0.9.2` 또는 `open`), 그리고 `report state <제목>`이 한 줄만 찍습니다(`done 0.9.2` · `open` · `asked: <이유>`). 바꾸는 것은 `report done` · `report undone` · `report ask <제목> > <이유>`이며, **메모를 다시 저장하면 저절로 열립니다** — 새로 적은 말은 다음 버전의 일거리이기 때문입니다.
- **작성 버전**은 저장할 때 `reports/written.mark`에 제목과 함께 적힙니다(메모 파일 자체는 사용자가 쓴 글 그대로 둡니다). 폴더의 버전은 `reports/version.mark`입니다 — 0.9.1까지는 둘 다 `.txt`라 **목록에 `version`·`written`이라는 메모로 섞여 나왔고**, 그 `version`을 지우면 다음 호출이 폴더를 다른 버전 것으로 보고 **메모를 전부 지웠습니다**(0.9.2에서 고침). 예전 파일은 내용이 이 형식일 때만 조용히 옮기고, 사용자가 쓴 글이면 메모로 남겨 둡니다.
- **지워지지 않습니다**(0.9.2, 사용자 규칙): 버전으로 쓸어내던 기능은 없앴습니다. 메모는 누가 지울 때까지 남고, `report erase`로 하나씩 또는 `report sweep`으로 **이미 반영된 것만** 한꺼번에 지웁니다([Y/n], `-proceed`로 바로).
- **터미널에서**(0.9.2): 에이전트는 앱 콘솔을 쓸 수 없으므로 저장소 루트의 **`report.ps1`**이 같은 일을 합니다 — `.\report.ps1 open`(아직 처리 안 한 메모를 본문까지), `list`, `read <제목>`, `state <제목>`, `done <제목>`, `undone <제목>`, `asked <제목> -Why "<이유>"`. `list`·`open`은 맨 위에 **이유를 어느 언어로 적어야 하는지**를 설정에서 읽어 찍습니다. 반영 표시를 바꾸는 길은 이 스크립트와 콘솔뿐이라 누가 하든 같은 모양으로 남습니다.

## 진행률 바 (0.9.2, 사용자 요청)

시간이 걸리는 명령은 프롬프트 대신 막대를 띄우고, **끝날 때까지 `Visualizer>`를 내놓지 않습니다** — 그래서 그동안 다른 명령을 칠 수 없습니다(터미널과 같게).

- 모양: `Map sync  [#########...............]  38%` — 이름, 24칸 막대, 퍼센트.
- 대상: `sync`, `sync rebuild`, `install linker`. `link`는 모드가 이미 보낸 값을 읽을 뿐이라 기다리지 않으므로 막대가 없습니다.
- 그동안 통하는 것은 **Ctrl+T**(최소화)와 복사뿐이고, 어떤 경로로 들어온 입력도 큐에 쌓지 않고 버립니다.
- 끝나면 결과 줄을 적고 프롬프트가 돌아옵니다(`Map sync finished. 57 songs in the library.`).
- 메인 화면 설명줄은 같은 진행률을 **퍼센트**로 적습니다(`동기화 중… 38%`). 복사가 앞의 40%, 세 폴더 파싱이 나머지 60%입니다.

## 설정 이름 (`구역.설정`)

설정 창에 행이 있는 설정만 이름이 있습니다(자동 진행 토글·곡 리스트 표시 항목·레인 배경색은 제외). 값: 토글은 `true`/`false`
(`set`은 `on/off`·`1/0`·`able/disable`도 받음), 비울 수 있는 키는 `none`, 데이터 폴더 기본값은 `auto`, 패턴·색·경로처럼 공백이
있는 값은 줄 끝까지입니다. 설정 창에서 잠기는 항목은 `set`도 거부합니다(예: `tiles.instant-down`이 false면 `tiles.down-notes`).
`experimental` 구역은 `advanced.experimental`이 true일 때만 보이며, 이 버전에는 설정이 없어 "This version has no experimental
features."라고 답합니다.

| 구역 | 설정 |
| --- | --- |
| `general` | language, theme, information-buttons, song-info, game-only, autosave |
| `songs` | search, sort, high-first, group (`base` · `custom` · `edited` · `base-custom`(기본값) · `custom-edited` · `base-edited` · `all`) |
| `checkpoint` | auto, auto-on-load |
| `keys` | reset, start, toggle, previous, next, settings, lane-1 – lane-8 |
| `tiles` | max, min, dynamic, 8k-6k, 6k-4k, 4k-2k, 2k-1k, instant-down, down-notes, pattern-8k, pattern-6k, pattern-4k, pattern-2k, initial, order-8k, order-6k, order-4k, order-2k |
| `timing` | speed, chord-window, no-center-pair, chord-side(`none`·`1-2`·`3-4`·`1-2,3-4`(기본값)), trill-window, no-trill-center-pair, trill-side(chord-side와 같은 값), start-delay, calibration, pause-align |
| `judgment` | bar, window, average, average-notes |
| `magic` | notes, max-interval, fade-in, lead, fade-out, fixed, fixed-count |
| `design` | display-lanes(`max`(기본값)·`1`·`2`·`4`·`6`·`8`, 최대 타일 수보다 작으면 거절) |
| `display` | travel, line-height, press-window, thickness, width, hold-width, offset, input-effect, miss-effect |
| `colors` | lane-effect, note, chord, checkpoint, judgment-line, miss, pressed-transparency |
| `advanced` | experimental, extra-lanes, slowdown, tile-order, chord-lanes, keep-speed, next-gap, map-folder, data-folder, process |
| `experimental` | chord-before, run-restart |

0.6.1 이름 변경: `experimental.center-pair`(허용, 기본 false) → `timing.no-center-pair`(불가, 기본 true, 뜻이 반대), `experimental.chord-side` → `timing.chord-side`, `experimental.chord-lanes` → `advanced.chord-lanes`. 새 설정 `advanced.keep-speed`.

새 설정을 추가하면 `SettingPaths`에 이름을 넣어야 `CmdConsoleTests`의 "every setting has a name" 검사를 통과합니다.
