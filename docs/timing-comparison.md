# 타이밍 대조

세 가지를 구분합니다: **자동 테스트**, **독립 기준 계산 대조**(이 문서), **게임 자체와의 대조**(게임 링크 `link tiles`, [게임 동기화](game-sync-plan.md))와 실제 플레이.

## 독립 기준 계산기

`tests/Visualizer.Tests/GameRuleTimingComparison.cs`는 운영 파서를 부르지 않는 테스트 전용 계산기입니다. 설치된 `Assembly-CSharp.dll`을 ReflectionOnly로 읽어 확인한 규칙을 float/radian으로 따로 구현합니다(파서는 double/degree).

- `scrLevelMaker.InstantiateFloatFloors`: float 각도에서 radian 입·출구 각도, 중간 회전.
- `scnGame.ApplyCoreEventsToFloors`: BPM 배율, 회전 방향, Pause·Hold·FreeRoam 추가 박자.
- `scrLevelMaker.CalculateFloorEntryTimes`: floor별 도착 시간 누적, 거의 0/360도 처리.
- `scrMisc.GetTimeBetweenAngles` / `GetAngleMoved`: 게임 radian 상수·modulo·BPM 환산.
- hit 종류(자동·hold 시작·hold 끝)도 따로 계산해 대조합니다.

확인한 게임 DLL 해시가 다르면 비교를 거부합니다. 게임이 업데이트되면 위 함수와 `scnGame.ApplyEventsToFloors`·`scrConductor.Update`·`ffxCheckpoint`의 IL을 `.local/il-*.txt`의 이전 덤프와 견줘 보고 나서 해시를 바꿉니다(새 빌드 덤프는 `.local/il-build-<날짜>/`): 2026-11-04 빌드(`EC0D7C38…ADA5BC`)는 `CalculateFloorEntryTimes`·`CalculateSingleFloorAngleLength`·`InstantiateFloatFloors`·`GetInverseAnglePerBeatMultiplanet`·`HandlePause`가 그대로이고, `ApplyCoreEventsToFloors`는 빈 목록 검사가 null 검사로 바뀐 것뿐, `ApplyEventsToFloors`에 더해진 `shouldBeAutoPlayed`·`PlayerHitFloors`는 점수·결과 화면만 읽으며, 체크포인트는 `docs/checkpoints.md` 그대로입니다(2026-11-12). 양쪽 모두 첫 hit를 원점으로 맞추므로 음악 offset·카운트다운은 비교 대상이 아닙니다. 출구 각도 곱은 double로 유지합니다(Search Party 헤어핀을 사용자가 게임에서 확인 — IL 근거가 아님). **같은 IL을 읽은 구현이라 같은 누락을 공유할 수 있으므로**(いますぐ輪廻), 대조 통과는 게임 실행 시각과 맞는다는 뜻이 아닙니다.

허용 기준은 1ms입니다. 지금까지 대조한 곡(Plum - R, RUSH E, HeHeHe, Machinegun Psystyle, Megamix, Search Party, ENERGY SYNERGY MATRIX, Up In My Jam, 정령의 성, いますぐ輪廻 등)은 모두 최대 차이 0.012ms 이하입니다. **2026-10-12 Workshop 전체(63파일, 사용자 요청)**: 58곡 대조, 57곡 1ms 안(최대 0.08ms, He_He_He main). 계산기에 없던 규칙을 따로 넣음 — 쉼표 생략·여분 쉼표, Toggle 문자열(`"Enabled"`), 미드스핀을 건너는 롱노트와 연속 롱노트(`HoldChain`), pitch(곡 시계 = 실제 × pitch). THE END OF HATSUNE MIKU(3011005213)는 그때 최대 3000.8ms(계산기에 MultiPlanet이 없었음) → 게임 자체 타일 시각으로 파서의 3행성 부호 오류를 찾아 고친 뒤 게임과 1450개 hit 오프셋 동일, 계산기(IL 그대로 MultiPlanet 추가)와 최대 0.002ms(2026-10-12, 이 곡의 로컬 사본만). PLUM MEGAMIX 2(2924697157)는 게임이 보낸 타일 시각 6545개와 오프셋 퍼짐 0.0ms(게임 값 대조). なきそ - いますぐ輪廻는 20ms 안에 hit 5개가 몰린 곳(62.9초)에서 기본 4레인 배치가 멈춤(규칙대로, 앱은 넓은 레인을 제안). 원본 63개 SHA-256·수정 시각 불변.

## 실행 (사용자가 요청할 때만)

```powershell
.\build.ps1 -Configuration Release -Test -ReadOnlyMapRoot '<맵 루트>' -CompareGameRules
```

결과는 `.local/test-artifacts/`에만 씁니다: `timing-comparison.md`(요약), `timing-map-N-hits.csv`(hit별 floor·양쪽 상대 시각·차이), `timing-map-N-checkpoints.csv`(시작점별 활성화 시각·구간 최대 오차). 원본은 읽기만 하며 SHA-256·수정 시각 불변을 검사합니다. 한 곡만 볼 때는 [디버그 기록](debug-logs/README.md)의 사본 절차를 씁니다.

## 실제 플레이로 확인할 때

- 표시 보정을 0ms로 두고 같은 곡·같은 시작 구간을 고릅니다. 게임 링크 없이 할 때는 시작 키를 **실제 첫 타격 순간**에 누릅니다(카운트다운 시작이 아님).
- 처음부터 끝까지 같은 양만큼 어긋나면 시작 기준이나 표시 지연이고, 시간이 지날수록 커지거나 특정 변속·Pause에서 갑자기 생기면 구간 타이밍 문제입니다([timing-mismatch](debug-logs/timing-mismatch.md)). 일정한 차이에만 표시 보정을 쓰고, 구간마다 다른 오차를 보정값으로 숨기지 않습니다.
- 게임 링크가 있으면 `link tiles`가 모든 타일의 차이를 한 번에 보여 줍니다(한 맵 1360타일 전부 같은 오프셋, 퍼짐 0.0ms 확인).

## 2026-10-12 · THE END OF HATSUNE MIKU 재제보 대조

사용자 명시 승인으로 이 곡의 앱 저장소 맵을 프로젝트 내부 임시 폴더에 복사해 한 곡만 `build.ps1 -Test -ReadOnlyMapRoot <사본> -CompareGameRules`로 검사함. 파서 v25, hit 1450개·누르는 노트 1385개·체크포인트 9개. 독립 계산 최대 차이 **0.001784ms**, 간격 0.000154ms, 재시작 구간 0.002071ms, 체크포인트 활성화 0.001190ms. 268/268(일반 테스트 267개 + 한 곡 대조), 빌드 경고·오류 0.

재제보 당시 저장된 v24 캐시는 310번까지 v25와 같고, 311번부터 133.333ms 늦음(325번에서 1626.667ms). 원인은 수정 전 캐시의 재사용으로 확인: 앱 재시작·Map Sync·다시 불러오기 필요. 128번 타일은 정상이라고 사용자가 정정함. 원본 SHA-256·수정 시각 불변, 이번에 게임의 값을 새로 측정하지 않음. 자세한 기록은 [타이밍 제보](debug-logs/timing-mismatch.md).
