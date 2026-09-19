# BgFree — HDT 전장 플러그인

[Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker)(HDT)에서 Tier7 구독 전용이 된 전장 오버레이를
[Firestone](https://github.com/Zero-to-Heroes/firestone)의 공개 통계로 대신 채우는 플러그인입니다. 개인용 비공식 도구입니다.

## 기능

| 화면 | 표시 | 데이터 |
|---|---|---|
| 영웅 선택 | 영웅별 티어·평균 순위·픽률·순위 분포. MMR 구간, 로비 종족 반영(추정). 내 영웅별 성적 | Firestone hero-stats (솔로/듀오) + HDT 게임 기록 |
| 장신구 선택 | 장신구별 티어·평균 순위·픽률 | Firestone trinket-stats |
| 세션 패널 | 이번 로비 종족으로 가능한 조합의 점유율·평균 순위 (솔로) | Firestone comp-stats |
| 영웅 선택 직후 | 내 영웅의 실제 1등 최종 보드 예시 (HDT Inspiration 패널, 전구 버튼으로 다시 열기) | Firestone comp-stats 최종 보드 |
| 선술집 | 하수인별 구매 시 순위 기대차(`+0.6`), 내 보드로 추정한 조합과의 궁합(`★` / `✕`), 예상 조합 | Firestone card-stats + 1등 보드 빈도 |
| 선술집 | HDT 선술집 핀 패널 활성화 | HDT |

HDT가 이미 무료로 주는 기능(Bob's Buddy, 상대 보드, 하수인 목록, 세션 기록)은 건드리지 않습니다.

## 설치

1. HDT를 설치하고 한 번 실행합니다.
2. [Releases](../../releases/latest)에서 `install.cmd`를 받아 더블클릭합니다. ("Windows의 PC 보호" 창이 뜨면 추가 정보 → 실행)

스크립트가 HDT를 닫고, 최신 `BgFree.dll`을 플러그인 폴더에 넣고, 플러그인을 활성화하고, HDT 자체 Tier7 오버레이를 끈 뒤 HDT를 다시 켭니다.

PowerShell을 쓴다면 한 줄로도 됩니다.

```powershell
irm https://raw.githubusercontent.com/Heinul/Hearthstone_Deck_Tracker_Battlegrounds_Plugin/main/install.ps1 | iex
```
이후 새 버전은 HDT 시작 시 자동으로 내려받고 다음 HDT 시작 때 적용됩니다.

수동 설치: [Releases](../../releases)의 `BgFree.dll`을 `%AppData%\HearthstoneDeckTracker\Plugins\BgFree\`에 두고 HDT 설정 → 플러그인에서 켭니다.

플러그인 설정 화면의 **Self-check** 버튼은 데이터 로드 상태와 HDT 내부 훅 상태를 보여줍니다.

## 빌드

```
dotnet build
```

HDT 설치 폴더(`%LocalAppData%\HearthstoneDeckTracker\app-1.57.12`)의 DLL을 참조합니다. HDT가 업데이트되면 `BgFree.csproj`의 `HdtDir`을 바꿉니다.
빌드 결과는 곧바로 HDT 플러그인 폴더에 놓입니다.

## 알아둘 점

- 통계는 Firestone 공개 CDN(`static.zerotoheroes.com`)에서 1시간(조합 6시간) 단위로 받아 디스크에 캐시합니다. 이 데이터에는 명시된 라이선스가 없습니다. 개인 사용 범위에서 쓰세요.
- 수치는 HSReplay Tier7과 다릅니다. 표본과 구간 정의가 다르기 때문입니다. 티어 문자는 평균 순위 구간을 이 플러그인이 임의로 나눈 것입니다.
- 듀오는 영웅 통계만 듀오 데이터입니다. 나머지는 솔로 데이터를 쓰며 화면에 표기됩니다.
- HDT 내부 뷰모델을 사용하고 일부는 리플렉션입니다. HDT 업데이트로 깨질 수 있으며, 그 경우 해당 기능만 조용히 꺼집니다.
- 게임 메모리는 HDT가 이미 읽는 범위(로비 종족, MMR) 외에는 읽지 않습니다. hsreplay.net에는 접근하지 않습니다.

## 라이선스

MIT. HDT, Firestone, Hearthstone은 각 저작권자의 소유입니다.
