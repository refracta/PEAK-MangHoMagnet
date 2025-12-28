# MangHoMagnet

디시인사이드 피크(PEAK) 갤러리의 망호 글을 스캔해서 `steam://joinlobby/...` 링크를 자동으로 입장하는 모드입니다.

## 주요 기능
- 갤러리 목록 페이지에서 `망호` 말머리 글만 추적
- 글 내부의 `steam://joinlobby/...` 링크 수집
- 새로운 링크 발견 시 자동 입장 (Valid 링크만, 기본 OFF)
- 로비 리스트 UI에서 링크와 원본 글 정보를 확인하고 선택 입장

## UI
- 토글 키: 기본 `F8`
- 로비 링크 + 원본 글 제목/URL 표시
- `Open Post`로 글 열기, `Join`으로 수동 입장
- UI 내부에서 스캔/자동입장 토글 가능
- 목록 표시: `글번호 / 제목 / 글쓴이 / 작성일 / 조회수` + 스팀 링크
- 유효성 표시: 초록(입장 가능), 노랑(가득 참), 파랑(확인 중), 빨강(유효하지 않음), 회색(확인 불가)

## 설정
BepInEx 설정 파일: `BepInEx/config/com.github.manghomagnet.cfg`

- `General.Enabled` : 모드 활성화
- `General.GalleryListUrl` : 갤러리 목록 URL
- `Filter.SubjectKeyword` : 말머리 필터 (기본값: 망호)
- `General.PollIntervalSeconds` : 스캔 주기(초)
- `General.MaxPostsPerPoll` : 한 번에 검사할 글 수
- `General.MaxLobbyEntries` : 저장할 로비 링크 최대 개수
- `General.ValidateLobbies` : Steam API로 로비 유효성 확인
- `General.ValidationMode` : None/FormatOnly/Steam (Steam은 게임 내 팝업이 뜰 수 있음)
- `General.ValidationIntervalSeconds` : 로비 유효성 재확인 간격(초)
- `General.ExpectedAppId` : 예상 Steam App ID (기본값: 3527290)
- `Join.AutoJoin` : 자동 입장 여부 (Valid 링크만, 기본 OFF)
- `Join.JoinCooldownSeconds` : 자동 입장 쿨다운(초)
- `Network.UserAgent` : 요청에 사용할 User-Agent
- `General.LogFoundLinks` : 링크 발견 로그 출력
- `UI.ToggleKey` : UI 토글 키
- `UI.ShowOnStart` : 시작 시 UI 표시
- `UI.WindowWidth` : UI 창 너비
- `UI.WindowHeight` : UI 창 높이
 
UI에서 `Next refresh`로 남은 타이머가 표시되며 0이 되면 자동으로 새로고침됩니다.

## 빌드
```sh
dotnet build -c Release
```

`Config.Build.user.props.template`을 복사해 `Config.Build.user.props`로 만들면  
빌드 후 BepInEx/plugins에 자동 복사가 가능합니다.
