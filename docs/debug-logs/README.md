# 디버그 기록

사용자가 "이 곡이 안 된다"고 알릴 때 다음 에이전트가 같은 길을 빨리 밟도록, 대표 증상 두 가지의 **진단 절차와 실제 해결 예시**를 남깁니다. 규칙 자체의 원본은 `AGENTS.md`와 `docs/adofai-parser.md`이고, 여기에는 "어떻게 찾았는가"만 적습니다.

| 문서 | 증상 | 예시 |
| --- | --- | --- |
| [unsupported-maps.md](unsupported-maps.md) | 곡 목록에 `Unsupported`(또는 `Error`)가 붙고 불러와지지 않음 | Up In My Jam(Hold 0 → FreeRoam → Release 미반영), 정령의 성(형식 3), He He He low effect(일부러 남김) |
| [timing-mismatch.md](timing-mismatch.md) | 불러와지지만 어느 지점부터 채보와 어긋남, 채보대로 쳐도 죽음 | ENERGY SYNERGY MATRIX(거의 0인 회전), いますぐ輪廻(꺼진 SetSpeed) |

**새 사례를 해결하면 해당 문서의 "해결 기록" 절에 날짜순(오래된 것부터)으로 항목을 더합니다.** 제목은 `### 날짜 · 곡 이름 (Workshop id, 앱 버전) — 한 줄 요약`이고, 원인과 해결을 그 문서의 기존 예시와 같은 형식으로 적습니다. 같은 곡이 다시 제보되면 그 곡의 항목 안에 날짜순으로 줄을 더합니다. 새 종류의 증상이면 문서를 하나 더 만들어 이 표에 올리고, 그 문서에도 같은 날짜순 규칙을 적어 둡니다.

## 두 문서에 공통인 도구와 규칙

- **캐시에서 이유 읽기**: 앱이 저장소 안에서 실행되면 캐시는 `.local/app-data/map-cache/library.json`입니다(저장 폴더를 바꿨으면 그 폴더의 `map-cache/`). `Status`는 0 Supported · 1 Unsupported · 2 Error, `Warnings[0]`이 이유, `ParserVersion`은 **그 곡을 동기화한 빌드의 파서**입니다. 콘솔 출력은 cp949라 한글·일본어 제목이 깨지므로 `PYTHONIOENCODING=utf-8`로 읽습니다.

  ```bash
  PYTHONIOENCODING=utf-8 python - <<'EOF'
  import json
  d = json.load(open('.local/app-data/map-cache/library.json', encoding='utf-8'))
  for e in d['Entries']:
      if e['Status'] != 0 or '찾는 제목' in e['SongName']:
          print(e['Status'], e['ParserVersion'], e['SongName'], e['RelativePath'], e['Warnings'][:1])
  EOF
  ```

- **맵은 읽기 전용**: Workshop 원본(`C:\Program Files (x86)\Steam\steamapps\workshop\content\977950\<id>\`)이나 앱 저장소의 `maps/` 사본은 읽기만 합니다. 한 곡을 검사할 때는 `.local/tmp/<이름>/<id>/main.adofai`로 **복사**하고, 원본 SHA-256·수정 시각을 적어 둔 뒤 끝나면 원본이 그대로인지 확인하고 사본을 지웁니다.
- **한 곡 대조**(사용자가 분석을 요청했을 때만): `build.ps1`로 Debug를 빌드한 뒤, 같은 환경 변수(`DOTNET_CLI_HOME`·`TEMP`·`APPDATA` 등을 `.local/` 아래로)로 `tests/Visualizer.Tests/bin/Debug/net9.0-windows/Visualizer.Tests.dll <산출물 폴더> --read-only-map-root <사본 폴더> --compare-game-rules`를 실행합니다. `RULE COMPARISON … max=…ms, worst floor=…` 줄이 파서와 독립 기준 계산기의 최대 차이입니다. 0.6.8부터 이 대조는 노트 준비보다 먼저 돌아, 기본 설정(최대 4키)으로 배치할 수 없는 곡도 결과가 나옵니다. 그 경우 그룹은 배치 실패로 `FAIL`이 찍히지만 타이밍과는 무관합니다.
- **게임 근거**: 게임 DLL은 `.local/inspect-*.ps1`로 **ReflectionOnly**로만 읽습니다(실행·참조·주입 금지).
  - `inspect-checkpoint.ps1 -TypeName <형> -MethodPattern '^<메서드>$'`: 메서드 IL 전체.
  - `inspect-active.ps1`·`inspect-freeroam.ps1`: 필드를 읽고 쓰는 메서드 찾기.
  - `inspect-il-refs.ps1 -Methods '<형>|<메서드>'`: 메서드가 참조하는 필드·메서드·문자열.

  결과는 `.local/il-*.txt`·`.local/*-refs.txt`에 남겨 문서에서 가리킵니다.
- **고친 뒤**:
  - 새 테스트가 **고치기 전 코드에서 실패하는지** 확인합니다. 임시로 되돌릴 때도 Python으로 고치고, PowerShell `Get-Content`/`Set-Content`/`Copy-Item`은 쓰지 않습니다.
  - 캐시 내용이 바뀌면 `AdofaiTimelineParser.CurrentVersion`을 올립니다(사용자에게 재동기화 안내가 뜸).
  - 사본으로 다시 대조합니다.
  - `docs/adofai-parser.md`·`docs/timing-comparison.md`·`AGENTS.md`·`docs/handoff-history.md`를 갱신합니다.
- **사용자에게 알릴 것**: 캐시가 바뀌므로 **Map Sync**가 필요합니다. 사용자가 앱을 실행 중이면 Release 출력 폴더가 잠겨 Release가 빌드되지 않으므로 **앱을 닫고 Release를 다시 빌드**해야 합니다(에이전트는 앱을 종료하지 않음). 자동 테스트·독립 계산 대조·실제 플레이는 구분해 보고합니다.
