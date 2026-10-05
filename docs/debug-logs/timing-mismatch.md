# 디버그 기록 2 — 타이밍이 채보와 어긋나는 곡

## 증상

- 곡은 불러와지고 앞부분은 맞습니다. 그런데 어느 지점(몇 %, 몇 번째 노트)부터 노트가 채보보다 일찍 또는 늦게 떨어집니다.
- 사용자는 흔히 "게임에서 채보대로 쳤는데 계속 죽는다"로 알려 옵니다.
- 앞부분이 맞고 한 지점부터 **같은 양만큼** 계속 밀린다면, 거의 항상 그 지점의 floor 하나(또는 몇 개)의 시간을 잘못 센 것입니다.

## 진단 순서

1. **지점을 floor로 바꿉니다.**
   - 사용자가 말한 %·노트 번호를 hit 번호로 옮깁니다. 콘솔 `note`가 hit 번호·시각·타일을 보여 줍니다.
   - hit 번호를 floor로 옮깁니다. hit n의 floor는 floor 1부터 센 n번째 **중간 회전(999)이 아닌** floor입니다. floor n의 시간은 floor n−1 회전의 결과이므로, 원인은 대개 그 floor와 바로 앞 floor에 있습니다.
2. **그 floor 앞뒤의 이벤트를 JSON 그대로 봅니다.** 요약하지 말고 속성 전체를 봅니다(いますぐ輪廻의 원인은 속성 하나였습니다).

   ```bash
   PYTHONIOENCODING=utf-8 python - <<'EOF'
   import json
   d = json.load(open(r'.local\tmp\<이름>\<id>\main.adofai', encoding='utf-8-sig'))
   angles = d.get('angleData')          # pathData 맵이면 따로 풀어야 함
   print(len(angles), sum(a == 999 for a in angles))
   for a in d['actions']:
       if 500 <= a['floor'] <= 560 and a['eventType'] in ('SetSpeed', 'Twirl', 'Pause', 'Hold', 'FreeRoam', 'Checkpoint', 'AutoPlayTiles'):
           print(json.dumps(a, ensure_ascii=False))
   print(angles[500:560])
   EOF
   ```

3. **사본으로 한 곡 대조**를 합니다(`README.md`). 결과로 둘 중 하나를 가립니다.
   - **파서와 기준 계산기가 다르면**: 어느 쪽 규칙이 게임과 다른지 IL로 가립니다(ENERGY SYNERGY MATRIX는 파서가 틀렸음). `worst floor`가 곧 원인 floor입니다.
   - **둘이 같으면**: 두 구현이 **같이 모르는** 규칙이 있다는 뜻입니다(いますぐ輪廻). 2번에서 본 이벤트의 속성 중 파서가 읽지 않는 것을 게임이 어떻게 읽는지 확인합니다.
4. **IL로 규칙을 확정합니다.** 필드를 읽는 메서드를 찾고(`inspect-active.ps1` 방식), 그 메서드의 IL 전체를 봅니다(`inspect-checkpoint.ps1 -TypeName … -MethodPattern …`). 확인된 만큼만 구현합니다.
5. **파서와 기준 계산기에 따로 넣습니다.** 계산기는 파서를 부르지 않는 별도 구현으로 유지합니다. 그다음:
   - 테스트를 넣고, 고치기 전 코드에서 실패하는지 확인합니다.
   - 사본으로 다시 대조합니다.
   - `CurrentVersion`을 올립니다.
6. **같은 패턴의 다른 곡을 찾습니다.** 앱 저장소의 `maps/` 사본(읽기만)에서 같은 모양을 검색하고, 영향받는 곡을 사용자에게 알립니다.

## 해결 기록 (날짜순)

**이 절은 해결한 날짜순(오래된 것부터)으로 쌓습니다. 새 사례는 맨 아래에 추가합니다** — 제목은 `### 날짜 · 곡 이름 (Workshop id, 앱 버전) — 한 줄 요약`이고, 본문에는 증상, 어긋나기 시작한 지점(hit 번호와 floor), 원인, 게임 근거(IL 덤프 파일 이름), 찾은 방법, 해결과 대조 결과를 아래 예시와 같은 형식으로 남깁니다.

### 2026-09-22 · Tanchiky - ENERGY SYNERGY MATRIX (Workshop 3481210158, 앱 0.6.6) — 거의 0인 회전

- **증상**: 19~21% 즈음에서 채보대로 쳐도 계속 죽고, 비주얼라이저가 채보와 조금 다르게 보임.
- **원인**
  - floor 154~158에 중간 회전 사이로 3.2e-6° 타일이 있어, floor 159→160의 회전이 3.2e-6°만 남습니다.
  - 게임(`scrLevelMaker.CalculateSingleFloorAngleLength`, `.local/il-CalculateSingleFloorAngleLength.txt`)은 회전을 라디안으로 잽니다. `|각도| ≤ 1e-6` 또는 `!(|각도| < 6.28318430717959)`이면 한 바퀴(2π)로 봅니다.
  - 파서는 도 단위 `< 1e-8`만 한 바퀴로 봐서 0ms로 셌습니다. 곡에 이런 곳이 5곳(floor 160·500·504·644·741) 있어 곳마다 375ms씩 앞당겨졌습니다.
- **찾은 방법**: 기준 계산기는 처음부터 게임 상수를 쓰고 있었으므로, 한 곡 대조가 바로 floor 160을 짚었습니다(진단 3번의 "다르면").
- **해결**: 파서를 라디안 1e-6 기준으로 고쳤습니다(파서 v17). 대조 결과 707 hit, 최대 0.000627ms입니다.

### 2026-09-23 · なきそ (NAKISO) - いますぐ輪廻 (Workshop 3801724085, 앱 0.6.8) — 에디터에서 끈 SetSpeed

- **증상**: 500번째 노트 이후부터 타이밍이 맞지 않음.
- **원인**
  - floor 529의 `SetSpeed`(Bpm 580)에 `"active": false`가 붙어 있습니다. 에디터에서 **꺼 둔 이벤트**입니다.
  - 게임의 `scnGame.ApplyEventsToFloors`는 첫 루프에서 이벤트를 floor별 목록에 넣을 때 `LevelEvent.active`가 거짓이면 건너뜁니다(IL 0x0055 `brfalse`, `.local/il-apply-events.txt`). 이 목록만 `ApplyCoreEventsToFloors`(SetSpeed·Twirl·Checkpoint·Hold·Pause·FreeRoam)로 갑니다.
  - `LevelEvent.Decode`는 JSON에 bool `false`가 있을 때만 `active`를 거짓으로 둡니다. 키가 없거나 bool이 아니면 참입니다(`.local/il-levelevent-decode.txt`).
  - 파서와 기준 계산기 모두 `active`를 읽지 않아, floor 529·530(30°+330°, 합쳐 한 바퀴)을 1160 대신 580 BPM으로 셌습니다. 그래서 hit 519부터 늦어지기 시작해 hit 520부터는 **103.448ms** 늦었습니다(전체 1308 hit 중 floor 529 = hit 518).
- **찾은 방법**
  - 처음 대조는 기본 설정 배치 실패로 대조까지 가지 못했습니다(이 곡은 최대 4키로 동타를 배치할 수 없음). 그래서 대조를 준비보다 먼저 돌리게 바꿨습니다.
  - 그전에 hit 518 = floor 529 근처의 SetSpeed를 JSON 그대로 출력해 `"active": false`를 발견했습니다.
  - `.local/inspect-active.ps1`로 `LevelEvent.active`를 읽는 곳을 찾았더니 타이밍 쪽은 `ApplyEventsToFloors`뿐이었고, 그 IL로 확정했습니다.
  - 고친 뒤 대조는 두 구현이 같은 규칙이라 최대 0.0011ms이고, 노트 638이 63000ms에서 62896.552ms로 옮겨졌습니다.
- **해결**
  - 파서는 `"active": false` 이벤트를 타이밍·체크포인트·자동 타일 계산에서 뺍니다. RepeatEvents·SetConditionalEvents의 태그 검사는 꺼진 이벤트도 계속 봅니다(더 엄격한 쪽).
  - 기준 계산기도 같은 규칙을 따로 넣었습니다(파서 v19).
- **다른 곡**: 앱 저장소의 맵 중 `"active": false`가 타이밍 이벤트에 붙은 곡은 이 곡(원곡과 수정본 1번)뿐이었습니다. 나머지는 Bloom·MoveTrack·MoveDecorations 같은 연출 이벤트라 타이밍이 바뀌지 않습니다.

### 2026-09-25 · Kubbi & Jonas Dam - Up In My Jam (Workshop 3012378858, 앱 0.6.11) — 미니게임 구간, **미해결**

- **증상**(사용자): 미니게임(FreeRoam)이 시작되는 시점과 화면이 맞지 않고, 구간이 끝난 뒤 나오는 노트가 **게임보다 빠릅니다**.
- **지금까지 확인한 것**
  - 맵(읽기 전용)에는 이 구간에 BPM 변화가 없습니다(194 고정). 우리 계산으로 구간은 floor 206(자동 타일) 71.44초에서 시작해 **64박 = 19.79초** 뒤 floor 207(91.24초)에서 노트가 다시 나옵니다. 직전 노트는 floor 205의 Hold(68.97초)입니다.
  - 게임의 공식도 같습니다. `ApplyCoreEventsToFloors`(이벤트 44)는 다음 타일이 있고 duration ≥ 2일 때 `a = angleLength / π`로 두고 `extraBeats += max(a, duration − a)`를 더합니다(`.local/il-core-apply.txt`). a = 1박이므로 1 + 63 = 64박입니다.
  - 구간 종료 판정은 `scrController.UpdateFreeroam`의 `songposition > 다음 타일 entryTime − (한 박 / speed) × freeroamEndEarlyBeats`입니다(`.local/il-update-freeroam.txt`). 격자에서 무엇을 하든 **끝나는 시각은 고정**입니다.
  - `countdownTicks`(이 맵은 6)는 `scrConductor.PlayHitTimes`와 연습 스크럽만 읽고 타이밍 계산에 들어가지 않습니다(`.local/countdown-refs.txt`).
  - 맵 자신의 연출도 64박입니다: floor 206의 `RepeatEvents`가 4박 간격 16회입니다.
- **따라서 지금 아는 범위에서는 우리 계산이 게임과 같습니다.** 자동 동기화는 구조상 불가능합니다(안전 규칙 6: 게임 메모리를 읽지 않으므로 미니게임이 실제로 시작·종료했는지 관찰할 수 없습니다).
- **다음 단계**: 0.6.11에서 구간 동안 화면에 "미니게임 · n초 뒤 노트"를 띄웁니다. 사용자가 이 표시와 게임을 비교해 **어느 쪽이 얼마나(몇 박·몇 초) 빠른지** 알려 주면, 그 크기를 단서로 규칙을 다시 찾습니다(예: 한 박 차이면 `a`를 더하는 위치, 몇 초 차이면 구간 길이 자체).

## 주의

- 기준 계산기(`tests/Visualizer.Tests/GameRuleTimingComparison.cs`)는 파서를 부르지 않는 별도 구현입니다. 하지만 같은 IL을 읽고 쓴 것이라 **같은 누락을 공유할 수 있습니다**(いますぐ輪廻). 그래서 대조 통과가 게임과 일치한다는 뜻은 아닙니다. 게임에서 실제로 맞는지는 사용자의 플레이로만 확인됩니다.
- 보고할 때는 자동 테스트 통과, 독립 계산 대조, 실제 플레이 확인을 구분합니다.
