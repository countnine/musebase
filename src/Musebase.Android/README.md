# Musebase.Android (Phase 2 — 가사 엔진 조립 + 앱 내 동기 가사 표시 + 다른 앱 위 오버레이)

`.NET for Android`(net8.0-android) 헤드. **아직 `Musebase.sln`에 포함하지 않는다** —
android 워크로드가 CI/모든 개발 머신에 없어도 메인 빌드가 깨지지 않게 하기 위함.
앱이 성숙하면 sln 등록 + `ci.yml`에 `dotnet workload install android` 단계를 추가한다.

골든룰: `Musebase.Core`/`Musebase.Engine`/`contracts/`는 수정 금지(.claude/agents/android.md).

## 구성

| 파일 | 역할 |
|---|---|
| `Services/MediaListenerService.cs` | `NotificationListenerService` — 알림 접근 권한의 앵커. 알림을 파싱하지 않고, `MediaSessionManager.GetActiveSessions(component)` 호출 자격만 제공. **앱 완전 종료용 `Unbind()`/`Rebind()`**(API 24+) — 알림 접근이 켜져 있으면 시스템이 이 서비스를 계속 바인드해 프로세스가 살아 있으므로, 종료 시 스스로 언바인드하고 앱을 다시 열 때 재바인드한다(권한 설정은 유지) |
| `Services/AndroidNowPlayingSource.cs` | **광고 여부**(`IsAdvertisement`/`IsAdvertisementChanged`)와 세션 패키지(`CurrentSourcePackage`)를 Android 전용 속성으로 노출 — `Engine.TrackInfo`를 건드리지 않기 위함(골든룰). 광고 판정은 `TrackInfo` 생성 여부와 **독립**으로 계산한다(광고 구간에 제목이 비면 `RefreshTrack`이 트랙을 안 만드는데 그때도 광고임은 알아야 한다). `INowPlayingSource`(Musebase.Engine 계약)의 Android 구현 — 세션 선택(재생 중 우선), 콜백+500ms 폴링, 위치 보간(+1초 미만 역행 흡수). **재생 소스 선택**: `SetSource(mode, includeVideoApps, preferredSources)`로 자동/특정 앱 패키지 고정, 자동 모드에서는 영상·브라우저 앱(YouTube·크롬 등) 기본 제외(YouTube Music은 음악이라 포함). **선호 음악 앱**(`preferredSources`)을 하나라도 고르면 자동 모드에서 **그 앱들만** 후보가 된다(팟캐스트·영상 앱 차단). 비우면 종전 규칙. 감지된 세션 목록은 `ActiveSessionPackages`로 노출(설정 화면용) |
| `Services/AndroidEngineDispatcher.cs` | `IEngineDispatcher`의 Android 구현 — 메인 Looper `Handler` 기반 Post/주기 타이머(WpfEngineDispatcher와 대칭) |
| `Services/AdDecision.cs` | **뮤트용 상태 기계** — **진입 디바운스**(150ms — 곡 전환 시 메타데이터가 잠깐 비는 것을 광고로 오인하지 않게, **이탈은 즉시**)와 **안전 상한**(기본 180초 — 넘기면 강제 복구 후 재진입 차단). 판정 규칙 자체(`AdSignals`)는 **`Musebase.Engine`으로 옮겼다** — Windows도 광고 구간에는 가사를 찾지 않아야 해서 같은 코드를 쓴다. 옮기면서 유닛 테스트가 붙었다(`AdSignalsTests`) |
| `Services/MediaVolumeMuter.cs` | **미디어 볼륨 제어** — `AudioManager.SetStreamVolume(Stream.Music, …)`. 안드로이드에는 앱별 음소거 API가 없어 **기기 전체 미디어 볼륨**이 내려간다. 진입 시 이미 0이면 손대지 않고, 뮤트 중 볼륨이 0이 아니게 되면(사용자 볼륨 키) 그 구간을 포기한다. **원래 볼륨을 볼륨을 내리기 전에 `SharedPreferences`에 기록**해 프로세스가 광고 도중 죽어도 다음 실행이 `RestoreOrphanedVolume()`으로 복구한다 |
| `Services/AdMuteController.cs` | 감지와 볼륨 제어를 잇는다 — Spotify(`com.spotify.music`) 세션에서만 동작. 1초 틱으로 사용자 개입 확인 + 안전 상한 시간 발동(신호가 계속 "광고"면 이벤트가 안 오므로 틱이 필요). **포그라운드 서비스를 만들지 않는다** — 알림 접근이 켜져 있으면 시스템이 `MediaListenerService`를 계속 바인드해 프로세스가 살아 있다 |
| `MusebaseApp.cs` | 커스텀 `Application` — `LyricsEngineFactory.Create`로 엔진 1회 조립(화면 회전에도 유지). 소스=레지스트리 전체(개인용), 번역=MyMemory(무키·무료 기본), 대상 언어=기기 로케일, 캐시=`FilesDir/translations.db`, 텔레메트리=Noop |
| `Services/OverlayService.cs` | 포그라운드 서비스 — `WindowManager`의 `TYPE_APPLICATION_OVERLAY` 뷰(하단 중앙, 반투명 둥근 카드)로 다른 앱 위에 가사 표시. 코디네이터 `CurrentLineChanged`/`LineProgressChanged` + 소스 `IsPlayingChanged`만 **구독**(엔진 재조립 안 함). 재생 중+라인 있을 때만 표시, 터치 완전 통과. Android 8+ 알림 채널. **알림바에 곡명 + 가사·번역 상태**를 표시하고(상태 변경 시 갱신, `SetOnlyAlertOnce`) **"번역 끄기/켜기"·"위치 이동"·"정지" 액션** 제공 — 번역 토글은 설정 화면에 들어가지 않고 바로 유료 사용량을 끊기 위한 단축키. **표시 방식**: 기본 위치를 시스템 바 인셋 위로 띄우고, 이동 모드 드래그(위치는 비율 저장·회전 대응)와 **버블(플로팅) 모드**(탭으로 펼치기/접기, 가장자리 자석, 상태별 테두리 색, peek)를 지원 — 아래 "오버레이 표시 방식" 절 |
| `Views/KaraokeTextView.cs` | 커스텀 뷰 — 베이스(흰색) 위에 채움색(노랑 `#FFEB3B`)을 진행 글자까지 클립해 덧그리는 **글자 단위 카라오케**. 태그(`InlineTimeTags.CharIndexAt`) 있으면 글자 위치, 없으면 라인 비율 폴백. 100ms 갱신 사이를 앵커+실시간으로 60fps 보간(`postInvalidateOnAnimation`). `StaticLayout`으로 멀티라인/가운데정렬 대응 |
| `MainActivity.cs` | **전체 가사 화면** — 곡의 모든 줄(원문+번역)을 그리고 현재 줄만 흰색·굵게 강조하며 화면 가운데로 자동 스크롤(Apple Music 가사 방식). 현재 줄은 `Lyrics.LineIndexesAt(재생위치+TimeDelay+오프셋)`으로 화면에서 직접 계산한다(엔진 변경 없음). 사용자가 손으로 스크롤하면 5초간 자동 스크롤을 양보. 하단에 **재생 컨트롤**(이전/재생·정지/다음 — `INowPlayingSource.GetControls()` 가용 여부에 따라 흐림), 상단은 곡명·상태 + **우상단 아이콘 바**(가사 검색·틀린 가사 표시·오버레이 토글·위치 이동·설정·앱 종료)로 접어 화면을 가사에 내준다. 번역이 나중에 도착하면 **뷰를 다시 만들지 않고 번역 줄만 갱신**해 스크롤·강조가 튀지 않는다. 권한은 **빠졌을 때만 배너**로 뜨고 탭하면 해당 설정으로 이동 |
| `SearchActivity.cs` | **가사 수동 검색·선택**(Windows 검색 창과 같은 흐름) — 제목·아티스트로 코어 `LyricsSearchService`를 호출해 제공자 결과를 품질 순으로 보여 주고, 고른 후보를 미리 본 뒤 `Coordinator.UseLyricsAsync`로 적용한다. 화면을 열면 현재 곡으로 자동 1회 검색. 틀린 가사 표시는 메인 화면 아이콘(`MarkWrongLyrics`) |
| `Services/StatusText.cs` | 엔진의 구조화 상태(`LyricsStatus`/`TranslationDisplayStatus`) → 한국어 문구 변환 공용 헬퍼. 메인 화면과 알림바가 같은 문구를 쓴다(Windows는 i18n 카탈로그가 담당) |
| `SettingsActivity.cs` | **설정 화면**(코드 UI, Exported=false) — **재생 소스**(자동 감지 / 현재 감지된 앱 목록에서 고정) + **영상·브라우저 앱 포함** 체크박스(기본 꺼짐) + 엔진 스피너(MyMemory/DeepL/Google Cloud Translation/끄기) + **선택한 엔진의 API 키**(키를 쓰는 엔진에서만 표시, 문구·값이 엔진을 따라감, 눈 토글) + 대상 언어(비면 로케일 기본) + **API 번역 사용** 체크박스(기본 켬 — 끄면 새 번역 요청 없이 캐시된 번역만, 유료 사용량 차단) + **선택 엔진 실패 시 무료(MyMemory) 자동 전환** 체크박스(기본 꺼짐, 켜면 공개 서버 전송 안내 표시) + **오버레이 표시 방식**(버블 모드 / 접혀 있어도 새 줄 peek) + **오버레이 스타일**(글자 크기·모서리·배경 불투명도 슬라이더, 배경 표시/페이드/글자단위 카라오케/대상 언어만 표시 체크, 색 4종 `#RRGGBB` 입력 — Windows 설정 [오버레이 스타일] 탭과 같은 항목·기본값). 화면은 **기능별 탭**(재생 소스 / 번역 / 오버레이)으로 나뉘고 저장 버튼은 아래 고정. 저장 시 `AndroidSettings`에 반영 → `MusebaseApp.ApplyTranslationSettings()`로 **재시작 없이** 엔진 재구성(새 엔진은 다음 곡부터) |
| `Services/AndroidSettings.cs` | `ISharedPreferences`(앱 private) 래퍼 — `PlaybackSource`/`IncludeVideoApps`/`TranslationEngine`/`DeeplApiKey`/`GoogleApiKey`/`TargetLanguage`/`TranslationFallbackToFree`/`ApiTranslationEnabled`/`OverlayBubbleMode`/`OverlayPeekOnNewLine`/`OverlayRatio`·`BubbleRatio`(위치 비율, `-1`=미설정)(엔진 id로 읽고 쓰는 `Get/SetTranslationApiKey` 포함). **앱 private 저장이며 디스크 암호화는 아님**(Windows DPAPI와 다름 — 루팅/백업 추출 시 평문 노출 가능). 직렬화 키는 영어 식별자 유지 |
| `AndroidManifest.xml` | INTERNET + `SYSTEM_ALERT_WINDOW` + `FOREGROUND_SERVICE`(+`_SPECIAL_USE`, Android 14) + `POST_NOTIFICATIONS`. `<service>`(리스너/오버레이)/`<activity>`(Main/Settings)/`<application android:name>`은 C# 특성에서 생성·병합 |

SQLite: `Microsoft.Data.Sqlite`(Musebase.Core 참조)가 `SQLitePCLRaw.bundle_e_sqlite3`를 통해
net8.0-android 네이티브 `libe_sqlite3.so`를 자동 포함하므로 별도 PackageReference/초기화가 필요 없다.

오버레이 서비스는 `specialUse` 포그라운드 타입을 쓴다. Google Play 심사는 매니페스트에
`PROPERTY_SPECIAL_USE_FGS_SUBTYPE` 프로퍼티를 요구하지만, 사이드로드/실기기 런타임에는
강제되지 않으므로 이 스파이크에는 넣지 않았다(스토어 배포 시 추가 필요).

## 번역 설정 사용법

메인 화면의 **"번역 설정"** 버튼 → 엔진 선택(MyMemory 무료·무키 / DeepL / Google Cloud Translation /
끄기), 키가 필요한 엔진이면 **그 엔진의 API 키**(입력란이 선택한 엔진을 따라가며, 엔진을 바꿔도
입력값은 엔진별로 보관된다) 입력, 필요하면 **대상 언어**(예: `KO`, `JA`, `EN-US`; 비우면 기기 로케일) 지정 후 **저장**.
저장하면 앱 재시작 없이 엔진이 재구성되며, **새 엔진은 다음 곡/재검색부터** 적용된다(현재 곡의
기존 번역 태그는 유지 — Windows와 동일 동작). API 키는 앱 private 영역에만 저장되고 디스크
암호화는 아니다(위 표 참고).

**API 번역 사용**을 끄면 새 번역 요청을 전혀 보내지 않고(유료 사용량 0) 이미 캐시된 번역만 표시한다.
엔진·키 설정은 그대로 보존되며, 다시 켜고 저장하면 **재생 중인 곡부터 즉시** 번역한다
(Windows 트레이의 "API 번역 사용" 토글과 같은 스위치 — 설정 키도 `ApiTranslationEnabled`로 동일).

**선택 엔진 실패 시 무료 전환**을 켜면 주 엔진이 실패(할당량·인증·네트워크)한 줄만 MyMemory로
다시 번역한다(`CompositeTranslator` 폴백 체인). 주 엔진이 이미 MyMemory면 체크해도 무시된다.
켜면 가사가 무료 번역 공개 서버로 전송될 수 있다는 점에 주의.

## 오버레이 표시 방식 (위치 이동 · 버블 모드)

화면 하단이 재생 앱의 컨트롤·제스처 바와 겹쳐 가사가 가려지는 문제를 세 가지로 다룬다.

- **시스템 바 회피(기본 동작)** — 기본 위치를 `WindowInsets`의 하단 인셋(제스처 바·내비게이션 바·
  디스플레이 컷아웃)만큼 위로 띄운다. API 30+는 `GetInsets(SystemBars|DisplayCutout)`,
  그 미만은 `SystemWindowInsetBottom` 폴백. 인셋을 얻기 전(뷰 부착 전)에는 24dp를 쓴다.
- **위치 이동** — 알림바의 **"위치 이동"** 액션(또는 메인 화면의 "오버레이 위치 이동" 버튼)을 누르면
  이동 모드로 들어가 밴드가 잠시 터치를 받는다(`FLAG_NOT_TOUCHABLE` 해제). 드래그로 옮기고 다시
  누르면 통과 상태로 복귀하며 위치를 저장한다. 재생 중이 아니어도 잡을 수 있도록 안내 문구를 띄운다.
- **버블(플로팅) 모드** — 설정 화면에서 켜면 밴드 대신 지름 56dp의 원형 버블만 떠 있고, **탭하면 밴드가
  펼쳐지고 다시 탭하면 접힌다**. **길게 누르면 퀵 메뉴**가 버블 옆에 열린다 — `앱 열기` /
  `API 번역 사용 ✓켬·✕꺼짐`(누르면 토글, 메뉴를 연 채 라벨이 갱신된다) / `오버레이 위치 이동 ✓이동 중`.
  메뉴 밖을 누르면 닫힌다(`FLAG_WATCH_OUTSIDE_TOUCH` → `ACTION_OUTSIDE`).
  버블은 드래그로 옮기고 놓으면 가까운 좌/우 가장자리에 붙는다(자석). 끌기 시작하면 화면 하단에
  **포켓(✕)** 이 뜨고, 그 위에 놓으면(겹치면 빨갛게 강조) **오버레이를 끈다**(= 알림의 "정지"와 같음).
  서비스에서 액티비티를 띄우므로 `NewTask`가 필요하다 — 백그라운드 실행 제한은 오버레이 권한이 있는
  앱에는 적용되지 않는다. **알림 본문을 터치해도 앱 화면이 열린다**(`SetContentIntent`).
  접힌 상태에서도 **새 가사 줄이 나오면 약 3초 펼쳤다 접는 peek**을 기본으로 하며 설정에서 끌 수 있다.

### 버블의 상태 표기 (`Views/BubbleStatusDrawable.cs`)

버블 하나에 세 가지를 **서로 다른 채널로** 싣는다. 한 채널(예전에는 테두리 색)에 여러 뜻을 겹치면
어느 쪽이 바뀐 건지 읽을 수 없다.

| 채널 | 뜻 | 표기 |
|---|---|---|
| 테두리 링 **색** | 가사 상태 | 검색 중 = 흐린 트랙 위로 **악센트 호가 회전**(플레이스토어 다운로드 링과 같은 감각, 1.1초/바퀴) / 찾음 = 가사 색(기본 흰색) / 못 찾음·무곡 = 회색 |
| 테두리 링 **줄 수** | 사용자가 오버레이를 **켜 두었는가** | 켜 뒀으면 같은 굵기·밝기의 링이 안쪽에 하나 더 생겨 **두 줄** / 껐으면 한 줄 |
| 우상단 점 | 번역 예외 | 한도·실패 = 주황 / API 꺼짐(`Disabled`) = 회색 / 정상·캐시로 다 채워짐 = **점 없음** |

**안쪽 채움은 상태를 나타내지 않는다** — 밴드와 같은 재질로 보이도록 오버레이 배경 설정(색·불투명도)을
그대로 따르고, 배경을 꺼 뒀으면 검정 45%를 쓴다. 처음에는 "표시 중 = 링 색으로 채움(음표 반전)"으로
만들었는데, 흰색으로 꽉 차서 **뒤의 화면이 가려졌다**. 채움은 가림과 직결되므로 상태 채널로 쓰지 않는다.
같은 이유로 배경 불투명도가 아주 낮아도 25%까지만 내려간다(완전 투명이면 밝은 화면 위에서 음표가 안 읽힌다).

- 링은 **굵기도 밝기도 상태에 따라 바뀌지 않는다**(2dp, 70%). 굵기·밝기로 구분하면 활성 상태의 선이
  도드라져 화면을 방해한다 — 달라지는 건 줄 수뿐이다.
**"켜 둠"과 "지금 그려지고 있음"은 다른 것이다.** 두 줄 표기는 앞의 것(`_bandExpanded`)을 따른다 —
가사가 없는 구간·일시정지에는 밴드가 잠깐 사라지는데, 그때 한 줄로 돌아가면 "내가 꺼진 건가?" 싶어
버블을 또 누르게 된다. 실제 표시 여부(`_bandVisible`)는 접근성 문구에만 쓴다.

그래서 **`_bandExpanded`는 사용자의 버블 탭 말고는 아무도 바꾸지 않는다.** 흔들던 것들을 모두 걷어냈다:

| 상황 | 예전 | 지금 |
|---|---|---|
| 곡이 바뀜 | 접힘(0.3.0부터) → 곡마다 다시 눌러야 했다 | 유지 |
| peek(새 줄을 3초 보여 주기) | `_bandExpanded`를 켰다 껐다 | 별도 플래그 `_peeking` |
| 이동 모드 | 강제로 켜고 끝나면 껐다 | 건드리지 않는다(`UpdateVisibility`가 `_moveMode`만 봐도 밴드를 띄운다) |
| 설정 저장(`ActionRefreshDisplay`) | 접힘 | 유지 — 접는 건 버블 모드로 **막 들어왔을 때 한 번**뿐 |

- 회전은 `ValueAnimator`가 아니라 **핸들러 틱**(16ms)으로 돌린다. 개발자 옵션에서 애니메이션 배율을
  0으로 둔 기기에서는 `ValueAnimator`가 즉시 끝나 스피너가 멈춘다. 틱은 **검색 중에만** 돌고
  버블을 뗄 때 `Stop()`으로 확실히 멈춘다.
- 색은 사용자 팔레트를 따른다(찾음 = `OverlayTextColor`, 검색 호 = `OverlayKaraokeColor`).
- 상태 문구는 `ContentDescription`으로도 넣어 TalkBack이 읽는다.

**세로 모드에서는 가사 밴드를 항상 가로 가운데로 정렬한다** — 세로 화면에서는 밴드가 폭을 거의 다 쓰므로
좌우로 옮길 여지가 없고, 어긋나면 글자가 한쪽으로 몰려 보인다. 그래서 세로에서는 드래그로 **높이만** 바꾸고
X는 중앙 고정(`Gravity=Top|CenterHorizontal`, `X=0`)이며, 가로 모드에서는 좌우도 자유롭게 배치한다.
회전 시에는 `KaraokeTextView.SetMaxWidth`로 새 화면 폭을 다시 알려 줘야 줄바꿈·가운데 정렬이 맞는다
(생성 시 폭을 고정해 두면 회전 후 어긋난다).

위치는 픽셀이 아니라 **화면 여유 공간 대비 비율**(`OverlayRatio`/`BubbleRatio`)로 저장하고
`OnConfigurationChanged`에서 다시 계산하므로, 화면을 돌리거나 해상도가 바뀌어도 화면 밖으로 나가지 않는다
(세로 밴드의 X 비율은 "중앙"을 뜻하는 0.5로 저장한다).
밴드는 창 크기가 확정된 뒤에야 좌표를 계산할 수 있어, 저장된 위치는 **처음 보이는 시점에 1회** 적용한다.

## 잠금화면 가사 (알림)

**플로팅 오버레이는 잠금화면 위에 뜰 수 없다.** Android 8부터 `TYPE_APPLICATION_OVERLAY`는 키가드보다
아래 레이어로 고정됐고, 그 위로 올라가던 `TYPE_SYSTEM_OVERLAY`는 일반 앱에서 제거됐다 — 오버레이
권한이 있어도 예외가 없다. 대안은 두 가지뿐이고(① 알림, ② `setShowWhenLocked` 액티비티로 잠금화면을
통째로 덮기), 여기서는 **①**을 쓴다.

포그라운드 알림은 이미 `Visibility.Public`이라 잠금화면에 내용이 그대로 나온다. 여기에 현재 줄을 싣는다:

- 접힌 알림 = 원문 한 줄, 펼친 알림(`BigTextStyle`) = **원문 + 번역**
- 설정 [오버레이] 탭의 **"알림에 현재 가사 표시"** 로 끌 수 있다(기본 켬). 끄면 곡명·상태만 —
  듣는 내용이 잠금화면에 남지 않는다.
- 갱신은 **최소 400ms 간격**으로 묶는다(`NotifyMinIntervalMs`). 시스템이 알림 갱신 빈도를 제한해
  너무 잦으면 갱신이 통째로 버려지는데, 간주 표시 줄처럼 짧은 줄이 연달아 나올 때 그렇게 된다.
  간격 안에 또 바뀌면 예약해 두고 **마지막 값 하나만** 뒤늦게 그린다.
- 카라오케 채움·색은 알림에서는 불가능하다(순수 텍스트).

**왜 오버레이가 아니라 알림인가** — 확인해 봤고 다른 길이 없다.
`TYPE_APPLICATION_OVERLAY`가 키가드 위로 못 올라가는 것은 보안 목적의 의도된 제약이라 오버레이
권한으로도 예외가 없다. **AOD는 더 막혀 있다** — 삼성이 AOD API를 서드파티에 열지 않아, 스토어의
"AOD 앱"들은 실제 저전력 AOD가 아니라 검은 화면 액티비티를 켜 두는 흉내다(배터리 소모가 크다).
남는 방법은 `setShowWhenLocked` 액티비티로 잠금화면을 통째로 덮는 것뿐인데, 시계·알림을 가리고
백그라운드 실행 정책에 걸릴 위험이 있어 알림 쪽을 택했다.

## 설정 화면 탭

`SettingsActivity`는 리소스 XML 없이 코드로 만든 버튼 줄 + 섹션 표시 전환이다(Windows 설정 창과 같은 구성):
**소스 / 번역 / 오버레이 / 광고 / 정보**.

[정보] 탭은 Windows [정보] 탭과 같은 내용(앱 이름·버전·출처·라이선스·링크)에 안드로이드에서만 의미 있는
줄을 더한다 — 패키지명·OS·기기를 한 덩어리로 보여 주고 **탭하면 버전까지 붙여 클립보드로 복사**한다
(문제를 알릴 때 쓰라고). 버전은 하드코딩하지 않고 `PackageManager.GetPackageInfo`에서 읽으므로
릴리스마다 손댈 곳이 없다(`ApplicationDisplayVersion`/`ApplicationVersion`이 그대로 나온다).

## 빌드 환경 (2026-07-26 이 머신에서 확인한 상태 — APK 산출까지 성공)

- .NET SDK 8.0.423 (`C:\Program Files\dotnet`)
- **android 워크로드: 설치됨** (`dotnet workload install android` — Microsoft.Android.Sdk.Windows 34.0.154)
- **JDK: Microsoft OpenJDK 17** — `C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot` (`winget install Microsoft.OpenJDK.17`)
- **Android SDK: `%LOCALAPPDATA%\Android\Sdk`** — platforms;android-34, build-tools 34.0.0/35.0.0, platform-tools, cmdline-tools

주의: JDK가 없으면 `sdkmanager`(자바 프로그램)가 못 돌아서 SDK 구성요소 설치가 조용히 실패하고
`Dependency 'platforms;android-34' should have been installed but could not be resolved` 경고만 남는다.
**JDK부터 깔 것.**

### PATH·환경변수 설정 (한 번만 — 이후 `-p:` 인자 없이 빌드)

`JAVA_HOME`/`ANDROID_HOME`이 있으면 .NET for Android가 알아서 찾는다. PowerShell에서 **사용자 환경변수**로
영구 등록(관리자 불필요, 새 터미널부터 적용):

```powershell
[Environment]::SetEnvironmentVariable('JAVA_HOME', 'C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot', 'User')
[Environment]::SetEnvironmentVariable('ANDROID_HOME', "$env:LOCALAPPDATA\Android\Sdk", 'User')
# PATH에 dotnet + java + adb 추가 (기존 사용자 PATH 뒤에 덧붙임)
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
$add = @('C:\Program Files\dotnet',
         'C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot\bin',
         "$env:LOCALAPPDATA\Android\Sdk\platform-tools") | Where-Object { $p -notlike "*$_*" }
[Environment]::SetEnvironmentVariable('Path', (@($p) + $add -join ';'), 'User')
```

현재 세션에만 적용하려면:

```powershell
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:Path += ";C:\Program Files\dotnet;$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools"
```

### 빌드

```powershell
dotnet build src/Musebase.Android -c Debug           # 환경변수를 설정했다면 이걸로 끝
```

환경변수를 안 쓰면 매번 경로를 넘긴다:

```powershell
dotnet build src/Musebase.Android -c Debug `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot"
```

처음부터 다시 까는 머신이라면 JDK 설치 후 자동 설치 타깃으로 SDK를 채울 수 있다
(관리자 불필요, 사용자 폴더에 설치):

```powershell
dotnet build src/Musebase.Android -t:InstallAndroidDependencies -f net8.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot" `
  -p:AcceptAndroidSDKLicenses=true
```

수동 설치 대안: Microsoft OpenJDK 17(msi) + Android Studio(또는 commandline-tools)로
`%LOCALAPPDATA%\Android\Sdk`에 platform-tools/android-34 설치 후 `JAVA_HOME`/`ANDROID_HOME` 설정.

APK 산출 경로(디버그 서명 포함): `src/Musebase.Android/bin/Debug/net8.0-android/com.countnine.musebase-Signed.apk`

## 폰에서 테스트 (사이드로드)

1. 폰 USB 디버깅 켜고 `adb install <위 APK 경로>` — 또는 APK를 폰에 복사해 설치
   (출처를 알 수 없는 앱 허용 필요).
2. Musebase 앱 실행 → "알림 접근 권한 설정 열기" 버튼 → 설정 목록에서 **Musebase** 토글 ON.
   또한 Android 13+에서는 **"알림 표시 권한 허용"** 도 눌러 허용해야 한다 — 거부 상태면
   곡명·번역 상태를 담은 포그라운드 알림이 아예 뜨지 않고(오버레이는 정상 동작),
   시스템이 띄우는 "다른 앱 위에 표시됨" 알림만 남는다.
   (설정 경로: 설정 > 알림 > 기기 및 앱 알림 접근 — 기종에 따라 다름)
3. 앱으로 돌아오면 "알림 접근: 허용됨 ✓" 표시. YouTube Music/Spotify/멜론 등에서 재생 시작.
4. 1초 이내에 곡명/아티스트/위치/소스앱이 화면에 갱신되면 감지 성공.
   위치가 초 단위로 흐르는지(보간 동작), 곡 넘김 시 즉시 바뀌는지 확인.
5. 가사 영역: "가사 검색 중…" → "가사: <소스> (품질 …)"로 바뀌고, 재생 위치에 맞춰
   현재 줄이 굵게 표시되면 성공. 같은 곡 재재생 시 "가사: 캐시 · <소스>"(오프라인 동작).
   번역 줄은 MyMemory(무키 무료) 상태에 따라 지연될 수 있다.
   화면 회전 후에도 가사가 유지되는지(엔진이 Application 소유) 확인.

## 폰에서 테스트 — 다른 앱 위 오버레이 + 글자 카라오케

1. 앱에서 **"오버레이 권한 허용"** 버튼 → 시스템의 "다른 앱 위에 표시" 화면에서 **Musebase** ON.
   돌아오면 "다른 앱 위 표시: 허용됨 ✓" 표시.
2. **"가사 오버레이 켜기"** 버튼 → 상태바에 "Musebase 가사 표시 중" 알림이 뜬다(정지 액션 포함).
3. 음악 앱(YouTube Music/Spotify/멜론 등)에서 재생 시작 → **홈 화면이나 그 음악 앱으로 나가도**
   화면 하단 중앙에 반투명 카드로 현재 가사 줄(+번역)이 떠 있으면 성공.
4. 재생 위치가 흐르면 현재 줄의 글자가 **왼쪽부터 노랑(#FFEB3B)으로 채워지면** 카라오케 성공.
   글자 타임태그가 있는 가사(대개 LRCLIB의 라인 싱크는 라인 단위 → 라인 비율 폴백으로 채워짐)면
   글자 단위로, 없으면 줄 진행 비율로 채워진다.
5. 일시정지하면 오버레이가 자연스럽게 사라지고(재생 재개 시 다시 표시), 곡을 넘기면 즉시
   새 줄로 바뀐다. 오버레이 카드 영역을 만져도 **터치가 아래 앱으로 통과**하는지 확인.
6. 알림의 "정지" 또는 앱의 **"가사 오버레이 끄기"**로 오버레이/서비스가 종료되는지 확인.

## 폰에서 테스트 — Spotify 광고 뮤트

설정 [광고 뮤트] 탭에서 켠다(기본 꺼짐). **앱별 음소거가 아니라 기기 전체 미디어 볼륨이
내려간다** — 안드로이드에는 다른 앱 볼륨만 조절하는 공개 API가 없다.

### 로그로 확인하기 — 삼성 기기는 설정이 하나 더 필요하다

`dumpsys media_session`은 metadata를 **description(제목/아티스트/앨범) 3개로만** 덤프한다.
`ADVERTISEMENT` 플래그도 `mediaId`도 거기엔 안 나온다. 그래서 앱이 찍는 `ad-signals` 로그가
사실상 유일한 프로브다.

**One UI(갤럭시)는 서드파티 앱 로그를 기본으로 막는다** — 아래 없이는 `Musebase` 태그가
logcat에 한 줄도 안 나온다(실측: SM-S947N / Android 16). 프레임워크 로그만 보이니
앱이 죽은 것처럼 오해하기 쉽다.

```powershell
adb shell setprop log.tag.Musebase VERBOSE   # 재부팅하면 초기화된다
adb logcat -s Musebase:V
```

실측 로그(SM-S947N / Android 16 / Spotify 9.1.68.1888 / 무료 계정, 광고 2연속 구간):

```
22:14:44.232 ad-signals: flag=1 mediaId='spotify:ad:147a046a…' artist='광고 • 1/2' album='' → ad=True
22:14:46.039 ad-mute: 광고 감지 → 미디어 볼륨 0
22:15:11.108 ad-mute: 광고 종료 → 볼륨 복구
22:15:11.542 ad-signals: flag=1 mediaId='spotify:ad:60f7d44e…' artist='광고 • 2/2' album='' → ad=True
22:15:13.117 ad-mute: 광고 감지 → 미디어 볼륨 0
22:15:38.187 ad-mute: 광고 종료 → 볼륨 복구
22:15:38.618 ad-signals: flag=0 mediaId='spotify:track:…' artist='Duran Duran' → ad=False
```

여기서 확인된 것:

- **`flag=1`이 실제로 온다** — 신호 ①(표준 광고 플래그)이 동작한다. `mediaId`도
  `spotify:ad:`로 시작해 신호 ②까지 함께 잡힌다.
- **아티스트는 `'광고 • 1/2'`** — Windows에서 가져온 폴백 ③(`artist=Spotify`)은 **매칭되지 않는다.**
  현지화 + 순번이 붙어 고정 문자열로 잡을 수 없다. ①②가 확실하므로 문제되지 않지만,
  폴백만 믿는 설계였다면 못 잡았을 것이다.
- 광고 구간은 25초짜리 2개 = 약 54초. Windows 실측(55~58초)과 일치하므로 안전 상한 180초가 적절하다.

### 신호 → 뮤트 지연 (실기기가 잡아낸 결함 2개)

로그의 `ad-signals` 시각과 `ad-mute` 시각 차이가 **광고 소리가 그대로 나는 시간**이다.
코드 리뷰로는 안 보이고 타이밍 로그로만 드러나는 것들이라 여기 남긴다.

| | 광고 1/2 | 광고 2/2 |
|---|---|---|
| 최초 | 1.81초 | 1.58초 |
| `IsPlayingChanged` 구독 후 | 0.60초 | 1.20초 |
| 디바운스 재평가 예약 후 | **0.39초** | **0.24초** |

디바운스 설계값(150ms)에 근접했다. 남은 건 Spotify의 메타데이터 전파 지연이다.

1. **`IsPlayingChanged`를 구독하지 않았다** — 판정이 `IsAdvertisement && IsPlaying`인데 재생
   상태 변화에 반응하지 않아, 연속 광고 사이에 재생이 잠깐 끊길 때 1초 틱까지 밀렸다.
2. **디바운스가 이벤트 구동과 맞물려 실질 1초가 됐다** — 첫 광고 판정에서 후보 시각만 찍고
   빠지는데 그 뒤 재평가를 아무도 예약하지 않았다. 150ms로 설계한 값이 다음 틱(1초)까지
   늘어난다. `ScheduleDebounceRecheck()`로 디바운스 만료 시점에 스스로 다시 보게 했다.
   (Windows판은 같은 구조지만 500ms 폴링이 항상 돌아 최대 지연이 500ms로 묻혀 있었다.)

### 사용자 볼륨 개입은 광고 구간 전체에 유지된다

뮤트 중 볼륨을 올리면 그 광고를 포기하는데, 처음엔 **세그먼트 단위로만** 풀렸다. 실측:

```
22:45:37.980 광고 1/2 감지 → 볼륨 0
22:45:46.831 볼륨이 4로 바뀌어 포기
22:46:03.700 광고 1/2 종료 → 사용자 볼륨 4 유지   ✓
22:46:05.226 광고 2/2 감지 → 미디어 볼륨 0        ✗ 18초 만에 다시 내리누름
```

"사용자와 볼륨을 다투지 않는다"는 원칙을 정확히 어기고 있었다. `_userOverrode`를 광고 구간
전체에 유지하고 **실제 음악이 돌아왔을 때만**(`IsPlaying && !IsAdvertisement`)
`ResetUserOverride()`로 지운다. `raw == false`로 지우면 광고 1/2·2/2 사이 1.3초 공백에서
풀려 같은 버그로 돌아간다.

> 테스트가 통과했다고 끝난 게 아니었다 — "볼륨을 올리면 포기하는가"는 통과했고,
> **그 다음에 무슨 일이 일어나는지** 봤을 때 드러난 결함이다.

### 로그는 실제로 한 일만 말해야 한다

위 수정 직후, 볼륨 샘플은 내내 `5`인데 로그는 `광고 감지 → 미디어 볼륨 0`을 찍고 있었다.
`Mute()`가 사용자 개입 구간이라 조용히 빠져나오는데 호출자가 결과와 무관하게 로그를 남긴 탓이다.
이 플랫폼에서는 **로그가 유일한 프로브**이므로(위 참고) 사실과 다르면 곧장 헛된 디버깅으로 이어진다.
`Mute()`/`Unmute()`가 실제로 볼륨을 바꿨는지 `bool`로 돌려주고, 호출자가 그에 맞춰 찍는다.

### prefs를 adb로 직접 고치지 말 것

`sed`로 `shared_prefs/musebase.xml`에 키를 넣다가 PowerShell이 표현식의 `|`를 파이프로 해석해
**따옴표 없는 속성**(`<int name=AdMuteMaxSeconds value=20 />`)이 들어갔고, XML 파싱이 실패해
앱이 **전 설정을 기본값으로 띄웠다**(`선호앱=(자동)`, `engine=google`→`mymemory`). 그 상태로
저장이 한 번만 일어났으면 가사 서버 토큰·API 키가 영구 소실된다.

설정 변경은 **설정 화면에서** 하고, 굳이 adb로 해야 하면 먼저 `cp`로 백업하고 넣은 뒤
`grep -c 'name=[^"]'`가 0인지 확인할 것.

### 연속 광고 사이는 뮤트를 유지한다 (일시정지 보류)

광고가 2개 연속일 때 그 사이 재생이 잠깐 끊긴다. 이를 "광고 아님"으로 보면 볼륨이 1.3초
돌아왔다가 다시 내려가 **광고 소리가 새어 나온다**(실측 `22:36:12.573` 복구 → `22:36:13.928` 재뮤트).

`1/2`·`2/2` 카운터를 파싱하는 방법도 있지만 쓰지 않았다. 로그를 보면 두 광고 사이에
`ad=False`가 찍힌 적이 없다 — 원인은 광고 종료가 아니라 **재생 공백**이다. 그래서 신호를
3상태로 두고(`AdSignal`) 재생이 멈춘 동안은 판정을 유지한다. 카운터 문구가 지역마다 다르고
Spotify가 표기를 바꿀 수도 있는 것과 달리, 이 방식은 언어·표기와 무관하다.

무한정 붙잡지는 않는다 — `AdDecision.PauseHold`(5초)를 넘기면 광고가 아니라고 보고 볼륨을
되돌린다. 사용자가 광고 중 일시정지하고 자리를 떠도 볼륨이 0으로 남지 않는다.

실측(수정 후): 광고 구간 50초 내내 `vol=0`, `볼륨 복구` 로그는 곡이 돌아올 때 **1회만**.
(수정 전에는 같은 구간에서 2회 — 광고 사이에 한 번 더.)

### 사용자가 볼륨을 올려도 다음 광고는 다시 뮤트한다

뮤트 중 볼륨을 올리면 그 광고는 포기하지만, **광고 `mediaId`가 바뀌면**(1/2 → 2/2)
개입 기억을 지우고 다시 내린다. 볼륨을 올린 것은 "그 광고를 듣겠다"는 뜻이지
"남은 광고를 전부 듣겠다"는 뜻이 아니기 때문이다.

재생 공백 동안 판정을 유지하므로 광고 세그먼트 전환에는 상태 변화가 없다 — 그래서
`AdvertisementId`가 필요하다(상태 전이로는 새 광고를 알 수 없다). 같은 이유로 소스가 발화하는
이벤트도 광고 여부가 아니라 **여부 또는 식별자**가 바뀔 때다(`AdvertisementChanged`) —
여부만 보면 1/2 → 2/2가 감지되지 않아 다음 폴링까지 최대 1초를 기다린다.

> **두 수정이 서로 간섭한 사례.** 위 "일시정지 보류"를 넣자 세그먼트 사이에 `Unmute()`가
> 호출되지 않게 됐는데, `_muted`를 내리는 곳이 `Unmute()`뿐이었다. 그래서 사용자가 볼륨을 올려
> 포기한 뒤에도 `_muted`가 true로 남아 ⓐ `Mute()`가 "이미 뮤트 중"으로 빠져나가 재뮤트가 막히고
> ⓑ 다음 틱의 `CheckUserOverride()`가 볼륨이 0이 아니라며 또 포기했다(실측 로그에
> `볼륨이 1로 바뀌어 포기` 다음에 `볼륨이 2로 바뀌어 포기`가 연달아 찍힌다).
> 포기할 때 `_muted`도 함께 내려야 한다 — 뮤트를 놓았으면 상태도 놓아야 한다.

볼륨 관찰은 로그와 무관하게 되므로 교차 검증에 쓴다:

```powershell
adb shell cmd media_session volume --stream 3 --get
```

검증 항목:

1. **뮤트/복구** — 무료 계정 재생 → 광고 진입 시 미디어 볼륨 0, 곡 복귀 시 원래 값. `dumpsys audio`로 확인
2. **사용자 우선권** — 광고 중 볼륨 키를 올리면 그 구간은 더 건드리지 않는지(로그에 "포기")
3. **이미 0이었을 때** — 광고 전에 직접 음소거해 뒀으면 광고 후에도 0으로 남는지
4. **볼륨을 남기지 않기** — (a) 앱 완전 종료(퀵 메뉴/앱 화면) → 복구
   (b) 광고 도중 `adb shell am force-stop com.countnine.musebase` → **앱을 다시 열면 복구**되는지.
   이게 가장 중요하다 — 기기 전체 볼륨이라 남으면 피해가 크다
5. **안전 상한** — [광고 뮤트] 탭에서 최대 뮤트 시간을 20초로 낮춰 광고 도중 강제 복구가 도는지
6. **회귀** — 기능을 끈 상태에서 가사·오버레이·번역이 종전과 같은지(옵인의 핵심)
7. **다른 앱** — YouTube Music 재생 중에는 아무 일도 없는지(Spotify 전용)

> `adb install -r bin/Debug/net8.0-android/com.countnine.musebase-Signed.apk`로 재설치.
> 오버레이 권한은 설치형이 아니라 사용자가 직접 켜는 특수 권한이라, 앱 재설치 후에도
> 다시 켜야 할 수 있다.
