# Windows 전역 입력 관찰

## 버튼의 지정되지 않은 키 반응 수정

기존 WPF Button은 포커스가 있을 때 Space/Enter로 Click을 실행하므로 Raw Input 바인딩과 별개로 재설정/시작이 호출될 수 있었습니다. [WPF ButtonBase의 기본 처리](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ButtonBase.cs)를 확인하고 재현 테스트를 추가했습니다.

메인 도구 버튼은 `PlaybackButton`을 사용하여 이 두 키를 기본 Click 처리로 넘기지 않습니다. 키를 Handled로 소비하지 않으며, 설정한 Raw Input 바인딩만 재생 키 명령을 실행합니다. 마우스 클릭은 유지합니다. J의 시작 및 F8의 화면 토글은 의도된 별도 명령입니다. 재설정 키를 R로 바꾸면 Enter는 더 이상 재설정을 실행하지 않습니다.

`WindowsRawInputMonitor`는 키보드 UsagePage=1 / Usage=6을 `RIDEV_INPUTSINK | RIDEV_DEVNOTIFY`로 등록합니다. WPF의 `HwndSource` hook이 WM_INPUT을 전달하며 일반 UI KeyDown은 재생 트리거로 사용하지 않습니다. INPUTSINK는 앱이 전경이 아니어도 raw input을 받도록 하는 공식 방식입니다. [Microsoft Raw Input 개요](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input), [RAWINPUTDEVICE flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice).

`GetRawInputData`로 현재 메시지를 읽고, 키를 놓는 이벤트와 반복 입력을 구분합니다. WM_INPUT은 handled로 소비하지 않으며 WPF/DefWindowProc의 정상 처리가 이어집니다. `RIDEV_NOLEGACY`, RegisterHotKey, 입력 합성 API는 사용하지 않습니다. 게임에 주입하는 DLL이나 메모리 접근도 없습니다. [GetRawInputData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getrawinputdata), [WM_INPUT 처리 규칙](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input), [RAWKEYBOARD](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawkeyboard).

`KeyPressTracker`는 설정한 키가 현재 눌려 있는지만 보관하고 키 입력 내역/문자열/로그를 저장하지 않습니다. 키를 놓거나 장치를 제거하면 상태를 제거합니다. 설정 적용 시 관찰 키를 갱신하고 종료 시 Raw Input 등록을 해제합니다.

## 레인 키 관찰

레인 키(기본 `A S D F J K L ;`)도 같은 Raw Input 경로로 관찰합니다. `KeyPressTracker.Transition`은 누름·뗌을 구분하고 키 반복은 새 누름으로 보지 않습니다. 장치가 제거되면 그 장치로 눌려 있던 키를 뗀 것으로 알립니다. 레인 효과를 위해 WM_INPUT을 handled로 소비하지 않으며 게임 입력을 막지 않습니다. 레인 키는 시작 키와만 같은 가상 키를 공유할 수 있고, Reset/토글/위아래 방향키(체크포인트 이동)/setting 단축키와 겹치면 설정 적용을 거부합니다. 좌우 방향키는 ADOFAI 판정 변경과 겹쳐 2026-09-14부터 예약하지 않습니다. Esc는 일시정지로 예약하며 관찰만 하므로 게임의 일시정지 메뉴도 그대로 열립니다. 체크포인트 이동 키는 setting → 조작 키에서 바꿀 수 있습니다(기본 ↓ 이전·↑ 다음).

**전역 입력 범위 (2026-09-14 오후, setting → 고급 설정).** 켜짐은 ADOFAI **또는 이 비주얼라이저(대화 상자 포함)**가 활성 창일 때 반응합니다(앱 0.0.3부터 기본값은 꺼짐). 비주얼라이저 확인은 전경 창의 프로세스 ID가 자기 프로세스인지만 비교하며 프로세스 핸들을 열지 않습니다. 꺼짐은 어느 앱이 활성이든 반응합니다. 입력 칸·곡 제목 칸 편집 중과 설정 창이 열린 동안에는 반응하지 않습니다. 일시정지 중 레인 키는 해당 레인 노트에 맞춘 재개로 쓰입니다([노트 규칙](note-rules.md)). 구간(연습 모드 타일)에서 시작할 때는 첫 노트 레인의 레인 키가 시작 명령을 겸하며, 전경 정책은 Start와 같습니다.

**여분 레인 배치 (2026-09-14).** 여분 레인은 새 키를 추가하지 않고, 그 레인의 타일 번호에 해당하는 기존 레인 키(기본 4키 화면이면 2번 S, 7번 L)로 누릅니다. 관찰 대상 키 목록은 기존과 같습니다.

레인 키는 게임 중 초당 여러 번 눌리므로 누를 때마다 전체 화면 바인딩을 갱신하거나 프로세스를 열거하지 않습니다. 전경 확인은 `GetForegroundWindow` → `GetWindowThreadProcessId` 후 창 핸들이 바뀌었거나 2초가 지난 경우에만 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName`으로 실행 파일 이름을 다시 읽습니다. 제한된 조회 권한이라 관리자 권한으로 실행된 게임도 확인할 수 있으며, 프로세스 메모리는 읽지 않습니다. 조회에 실패하면 전경이 아닌 것으로 봅니다.

`InputPolicy`는 기본적으로 Reset/Start를 ADOFAI가 전경일 때만 실행합니다. 전경 창의 PID와 프로세스 이름을 확인하며 게임 메모리는 읽지 않습니다. 프로세스 이름은 기본 `A Dance of Fire and Ice`이고 `.exe` 접미사 유무를 허용합니다. 실행 파일 이름이 다르면 Settings에서 변경할 수 있습니다. 전경 조회 실패는 허용으로 간주하지 않습니다. [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid).

F8/사용자 토글 키는 OFF에서도 접근할 수 있도록 전경 필터를 적용하지 않습니다. Settings Hotkey의 기본값은 null이며 원할 때 선택할 수 있습니다. 앱의 텍스트/콤보 입력 중이거나 설정 창이 열려 있으면 명령 반응을 잠시 막지만 OS 키 메시지를 차단하는 것은 아닙니다. UI 버튼은 전경 필터와 관계없이 사용할 수 있습니다.

Raw Input 등록 실패는 화면의 Global input 상태에 표시하고 UI 버튼을 유지합니다. 자동으로 입력 차단 방식으로 대체하지 않습니다. 전역 입력 범위(기본 OFF)를 켜도 이 비주얼라이저 창이 활성이면 J/Enter가 반응합니다. 입력 칸 편집 중에는 반응하지 않으므로 로컬 미리보기에는 버튼을 쓰거나 포커스를 칸 밖으로 옮깁니다.

## 검증 범위

단위 테스트로 키 반복/해제/장치 제거, 전경 필터, 토글 예외 및 키 중복을 검증합니다. Windows에서 보이지 않는 테스트 HWND에 실제 Raw Input 등록/해제를 수행했습니다. 테스트에서 키를 합성하거나 게임에 입력하지 않습니다.

ADOFAI가 실제 전경일 때 하드웨어 키가 두 앱에 함께 전달되는 종단 간 검증은 수행하지 않았습니다. 서로 다른 권한 수준, 원격 데스크톱, 게임의 독점 입력/전체화면 환경은 추가 확인 대상입니다. 이 항목들은 검증 한계이며 타이밍 파서의 정확성을 보장하지 않습니다.
