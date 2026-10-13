# 디버그 기록

사용자가 "이 곡이 안 된다"고 알릴 때의 **진단 절차와 해결 예시**입니다. 규칙 자체의 원본은 `AGENTS.md`와 `docs/adofai-parser.md`이고, 여기에는 "어떻게 찾는가"만 적습니다.

| 문서 | 증상 |
| --- | --- |
| [unsupported-maps.md](unsupported-maps.md) | 곡 목록에 `Unsupported`(또는 `Error`)가 붙고 불러와지지 않음 |
| [timing-mismatch.md](timing-mismatch.md) | 불러와지지만 어느 지점부터 채보와 어긋남, 채보대로 쳐도 죽음 |

새 사례를 해결하면 해당 문서의 "해결 기록" 맨 아래에 `### 날짜 · 곡 이름 (Workshop id) — 한 줄 요약`으로 짧게 더합니다(원인 · 게임 근거 · 해결). 새 종류의 증상이면 문서를 하나 더 만들어 이 표에 올립니다.

## 공통 도구와 규칙

- **캐시에서 이유 읽기**: 캐시는 `%LOCALAPPDATA%\AdofaiVisualizer\map-cache\library.json`(데이터 폴더를 바꿨으면 그 폴더). `Status`는 0 Supported · 1 Unsupported · 2 Error, `Warnings[0]`이 이유, `ParserVersion`은 그 곡을 동기화한 빌드의 파서입니다. 콘솔은 cp949라 `PYTHONIOENCODING=utf-8`로 읽습니다.

  ```bash
  PYTHONIOENCODING=utf-8 python - <<'EOF'
  import json, os
  d = json.load(open(os.path.expandvars(r'%LOCALAPPDATA%\AdofaiVisualizer\map-cache\library.json'), encoding='utf-8'))
  for e in d['Entries']:
      if e['Status'] != 0 or '찾는 제목' in e['SongName']:
          print(e['Status'], e['ParserVersion'], e['SongName'], e['RelativePath'], e['Warnings'][:1])
  EOF
  ```

- **맵은 읽기 전용**: Workshop 원본과 앱 저장소의 `maps/` 사본은 읽기만 합니다. 한 곡을 검사할 때는 `.local/tmp/<이름>/<id>/main.adofai`로 **복사**하고, 원본 SHA-256·수정 시각을 적어 두었다가 끝나면 그대로인지 확인하고 사본을 지웁니다.
- **한 곡 대조**(사용자가 요청했을 때만): Debug를 빌드한 뒤 같은 환경 변수로 `tests/Visualizer.Tests/bin/Debug/net9.0-windows/Visualizer.Tests.dll <산출물 폴더> --read-only-map-root <사본 폴더> --compare-game-rules`. `RULE COMPARISON … max=…ms, worst floor=…`가 파서와 독립 기준 계산기의 최대 차이입니다. 대조는 노트 준비보다 먼저 돌므로 배치 실패 곡도 결과가 나옵니다(그 `FAIL`은 타이밍과 무관).
- **게임 근거**: 게임 DLL은 `.local/inspect-*.ps1`로 ReflectionOnly로만 읽습니다(`inspect-checkpoint.ps1 -TypeName <형> -MethodPattern '^<메서드>$'`로 IL 전체, `inspect-active.ps1`·`inspect-freeroam.ps1`로 필드를 읽고 쓰는 메서드, `inspect-il-refs.ps1 -Methods '<형>|<메서드>'`로 참조). 결과는 `.local/il-*.txt`에 남깁니다.
- **고친 뒤**: 새 테스트가 고치기 전 코드에서 실패하는지 확인하고, 캐시 내용이 바뀌면 `AdofaiTimelineParser.CurrentVersion`을 올리고, 사본으로 다시 대조하고, `docs/adofai-parser.md`·`AGENTS.md`를 갱신합니다.
- **사용자에게 알릴 것**: 캐시가 바뀌면 **Map Sync**가 필요합니다. 앱이 실행 중이면 Release가 빌드되지 않으므로 앱을 닫고 Release를 다시 빌드해야 합니다(에이전트는 앱을 종료하지 않음). 자동 테스트·독립 계산 대조·실제 플레이는 구분해 보고합니다.
