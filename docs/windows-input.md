# Windows 전역 입력 관찰

## Raw Input

`WindowsRawInputMonitor`는 키보드(UsagePage=1, Usage=6)를 `RIDEV_INPUTSINK | RIDEV_DEVNOTIFY`로 등록합니다. INPUTSINK는 앱이 전경이 아니어도 raw input을 받는 공식 방식이고, WPF `HwndSource` hook이 WM_INPUT을 받습니다. `GetRawInputData`로 누름·뗌·반복을 구분하며 WM_INPUT을 handled로 소비하지 않아 게임과 WPF의 정상 처리가 이어집니다. `RIDEV_NOLEGACY`·RegisterHotKey·입력 합성 API·DLL 주입·메모리 접근은 쓰지 않습니다. 참고: [Raw Input 개요](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input), [RAWINPUTDEVICE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice), [GetRawInputData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getrawinputdata), [WM_INPUT](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input).

`KeyPressTracker`는 설정한 키가 지금 눌려 있는지만 보관하고 입력 내역·로그를 저장하지 않습니다. `Transition`은 누름·뗌을 구분하고 반복은 새 누름으로 보지 않으며, 장치가 빠지면 그 장치로 눌린 키를 뗀 것으로 봅니다. 설정 적용 때 관찰 키를 갱신하고 종료 때 등록을 해제합니다. 등록 실패는 화면의 입력 상태에 표시하고 UI 버튼은 그대로 씁니다(입력 차단 방식으로 대체하지 않음).

## 버튼의 Space/Enter

WPF Button은 포커스가 있으면 Space/Enter로 Click을 실행합니다([ButtonBase](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ButtonBase.cs)). 재생 표면의 버튼은 `PlaybackButton`/`PlaybackToggleButton`을 써서 이 두 키를 기본 Click으로 넘기지 않고(소비하지도 않음), Raw Input 바인딩만 재생 명령을 실행합니다.

## 키 규칙

- 레인 키(기본 `A S D F J K L ;`, 타일 1~8)는 시작 키와만 같은 키를 공유할 수 있고(같이 눌리면 Start 먼저), 초기화·켜기/끄기·체크포인트 키·설정 단축키와 겹치면 적용을 거부합니다. 여분 레인은 그 타일 번호의 기존 레인 키로 누릅니다.
- Esc는 일시정지로, Ctrl+T·Ctrl+E는 고정 단축키로 예약됩니다. 관찰만 하므로 게임도 Esc를 받습니다. 좌우 방향키는 게임 판정 변경과 겹쳐 예약하지 않습니다.
- **Ctrl+T는 관찰한 키가 처리합니다**(`MainWindow.ObservedConsoleKey`): 게임이든 이 앱의 어느 창이든 앞에 있으면(전역 입력 범위가 꺼져 있으면 어디서나) 한 번 토글하고, 모달 질문이 떠 있으면 기다립니다. 창들의 키 처리는 같은 누름을 두 번 세지 않도록 삼키기만 하고(`ConsoleKeyInWindow`), Raw Input을 못 쓸 때만 직접 토글합니다. 창의 키 처리는 한글 입력 모드에서 글자를 `Key.ImeProcessed`로 받으므로 단축키와 수정 창 키는 `KeyNames.Real`로 실제 키를 읽습니다(그래서 예전에는 앱이 앞에 있을 때 Ctrl+T가 듣지 않았음).
- **`link auto`가 켜져 있으면** 위치를 옮기는 키(초기화·시작·일시정지·이전/다음 체크포인트, 레인 키로 시작·재개)는 반응하지 않습니다(`InputPolicy.MovesPlayhead`, 사용자 규칙: 게임에서 Enter를 누르면 노트가 곡 처음으로 돌아왔음). 레인 키는 노트 표시만, 켜기/끄기·설정은 그대로.
- 켜기/끄기(F8)는 꺼진 상태에서도 쓸 수 있도록 전경 필터를 적용하지 않습니다.

## 전경 확인

- **전역 입력 범위**(기본 켜짐): ADOFAI 또는 이 앱(대화 상자 포함)이 전경일 때만 초기화·시작·체크포인트·레인 키에 반응합니다. 앱 확인은 전경 창의 PID가 자기 프로세스인지만 비교합니다.
- 입력 칸·곡 칸 편집 중, 설정판이 열린 동안, 콘솔 창이 활성일 때는 반응하지 않습니다(OS 키 메시지를 막는 것은 아님). UI 버튼은 전경과 무관하게 씁니다.
- 레인 키는 초당 여러 번 눌리므로 누를 때마다 바인딩 전체를 갱신하거나 프로세스를 열거하지 않습니다. `GetForegroundWindow` → `GetWindowThreadProcessId` 후, 창이 바뀌었거나 2초가 지났을 때만 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName`으로 실행 파일 이름을 읽습니다(관리자 권한 게임도 확인 가능, 메모리는 읽지 않음). 조회 실패는 전경이 아닌 것으로 봅니다. 프로세스 이름 기본값은 `A Dance of Fire and Ice`(`.exe` 유무 허용).

## 검증 범위

단위 테스트로 반복·해제·장치 제거, 전경 필터, 키 중복을 검증하고, 보이지 않는 테스트 HWND에 실제 등록·해제를 합니다(키를 합성하지 않음). 게임이 실제 전경일 때 하드웨어 키가 두 앱에 함께 전달되는 종단 간 검증, 서로 다른 권한 수준·원격 데스크톱·독점 전체화면은 확인하지 않았습니다.

## 입력 합성 예외: link inspect (1.0.4)

- 앱은 관찰만 한다는 원칙의 유일한 예외입니다(AGENTS 안전 규칙 6, 사용자 승인). `Infrastructure/Input/KeyboardInjector`가 `SendInput`으로 스캔 코드를 보내며, `MainWindow.Inspect.cs`의 `LinkInspection`만 부릅니다.
- 키를 보내기 직전마다 앞 창의 프로세스가 게임인지 확인합니다(`WindowsForegroundObserver.ForegroundProcessId`, 캐시 없음 — 다른 스레드에서 불림). 보낸 키는 Raw Input이 관찰하므로 비주얼라이저의 입력 효과로 보이고, 그동안 위치를 옮기는 키(시작 키 포함)는 무시합니다.
