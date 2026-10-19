# HMLOG-Cross — HMLOG for Windows / macOS / Linux

`tools\HMLOG`(WinForms, Windows 전용)는 그대로 두고, 같은 해석 코드를 쓰는 크로스플랫폼 판입니다. .NET 8 + [Avalonia](https://avaloniaui.net) 11.

## 구성
| 폴더 | 내용 |
|---|---|
| `HMLOG.Core` | `Decoder`, `CecDecoder`, `Edid`, `BinAsm`, `Summary`: `tools\HMLOG`의 파일을 **수정 없이** 복사한 것 (UI 의존 없음). 원본이 바뀌면 다시 복사합니다. |
| `HMLOG.App` | Avalonia 앱: 툴바, 필터, 상태 패널, 탭, 시리얼 입력 |
| `publish.ps1` | win-x64 / osx-arm64 / osx-x64 / linux-x64 빌드, macOS는 `.app` 묶음까지 |

## 빌드와 실행
```
dotnet run --project HMLOG.App -- [raw.log]
.\publish.ps1            # 모든 OS
.\publish.ps1 osx-arm64  # 하나만
HMLOG.App --decode raw.log out.txt   # 창 없이 해석
```
설정은 JSON 파일입니다: Windows `%APPDATA%\OWM\HMLOG\settings.json`, macOS `~/Library/Application Support/OWM/HMLOG/`, Linux `~/.config/OWM/HMLOG/`. 자동 저장 로그는 같은 폴더의 `logs\`입니다.

## 포트
Windows `COMn`, macOS `/dev/cu.usbserial-*`·`/dev/cu.usbmodem*`, Linux `/dev/ttyUSB*`·`ttyACM*`(dialout 그룹 필요). 속도는 921600 / 2000000 / 3000000 8N1.

## 변환 현황
| WinForms 판 | 이 판 |
|---|---|
| LOG 탭 (트랜잭션, 접기, 확장, 훈련 띠, 검색, 주소 필터, 상세, 비트 뷰) | 완료 |
| 상태 패널 LINK / SINK / SESSION | 완료 |
| SCDC (REGISTERS, FRL TRAINING 목록), HDCP, EDID(.bin 저장), DATA (STATISTICS, RAW) | 완료 |
| 연결(자동 재연결, 자동 저장), 열기/저장, HPD·OPTICAL 리셋, 다크/라이트, 설정 JSON | 완료 |
| FRL 레인 그리드, HDCP 단계 칩 그림 | 완료 (FRL TRAINING 아래 레인 그리드, HDCP 단계 칩) |
| CEC 탭 (이벤트, 파형), WAVE | 완료 (EVENTS 목록, WAVEFORM 커스텀 Render: 줌·드래그·커서 A/B·PNG) |
| BOARD 탭 (광 모듈 제어, PS8419 EQ/레지스터) | 완료 — OPTICAL(VCSEL/TIA 슬라이더, TIA 레인표), RETIMER(HPD 출력, EQ 읽기 전용, 레지스터 읽기·덤프). PS8419 쓰기는 의도적으로 없음 |
| 상태 패널의 TARGET 카드 (보드·빌드, 상태 줄 지연, 손실, 링크 오류) | 완료 |
| 아이콘 글꼴(Material Symbols), 창 폭에 따른 헤더 축소 | 완료 (원본과 같은 자리: 툴바 버튼, 줄 앞 그룹 아이콘 Port/Show/Only/Rows/Clear on/Find, 위·아래 화살표, 상태줄, EDID 저장, CEC 막대). 원본에도 아이콘이 없는 Zoom·Apply·Read 류는 글자만 |
| 보레이트 Auto 감지 / 동기 | 완료 (Auto: 3M·2M·921600 탐색, 로그가 4초 끊기면 재탐색, 고정 속도는 연결 시 동기) — 실제 보드 미검증 |

## Windows 판과 다른 점
- 설정을 레지스트리 대신 JSON에 저장합니다.
- 포트 이름 옆의 장치 설명(WMI)은 쓰지 않습니다(Windows 전용 API).
- 제목 표시줄·스크롤바의 다크 처리는 Avalonia 테마가 합니다(`dwmapi` 호출 없음).
- 글꼴은 Menlo → Consolas → DejaVu Sans Mono 순으로 고릅니다.

## 시리얼 쓰기와 개발용 환경변수
- `Rx.Write(byte[])`: 제어 프레임(`1bbbbrrr 0vvvvvvv`)을 로그와 같은 포트로 보냅니다(`TestSink`로 가로채기 가능).
- 캡처/시험용: `HMLOG_TAB`(탭), `HMLOG_SUB`(하위 탭 `i,j`), `HMLOG_SIZE`, `HMLOG_DARK=1`, `HMLOG_DEMO=tx|rx`(보드가 응답하는 것처럼 상태·CEC·PS8419 줄 주입, 쓰기는 버림).

## 화면 구조 (원본 MainForm과 같은 배치)
- 헤더 3줄: Port(포트·보레이트·Connect·Reconnect·Auto save | 열기·저장·내보내기·지우기·테마), View(Show/Only/Rows/Clear on 체크 칩), Find(검색·주소·시간·HPD out). 각 줄 앞에 아이콘 열.
- 왼쪽 패널 순서 TARGET → LINK → SINK → SESSION, 박스형 탭 LOG · SCDC · HDCP · EDID · CEC · DATA · BOARD, 아이콘 상태줄(Raw·Transactions·NACK·Cut off·FIFO overflow | Shown).
- 캡처용 `HMLOG_TAB`은 이 탭 순서의 번호입니다(CEC=4, DATA=5, BOARD=6).


## macOS: DMG 만들기
Windows에서 `.\publish.ps1 osx-arm64`(또는 `osx-x64`)로 `publish\HMLOG-osx-<rid>.app`을 만든 뒤, 그 `.app` 폴더와 `make_dmg.sh`를 맥으로 복사해 같은 폴더에서 `./make_dmg.sh`를 실행하면 `HMLOG-arm64.dmg` / `HMLOG-x64.dmg`가 나옵니다(`hdiutil` 사용, Applications 바로가기 포함). 스크립트가 실행 권한을 다시 주고 ad hoc 서명(`codesign -s -`)을 합니다. Apple Silicon은 서명 없는 코드를 실행하지 않기 때문입니다. 공증은 하지 않으므로 내려받은 사본은 처음에 우클릭 > 열기가 필요합니다. Windows에는 `hdiutil`이 없어서 DMG는 맥에서만 만들 수 있습니다.
