# PROGRESS — Musebase for Windows (구 LyricsX for Windows)

> **상태: windows-v0.17.0 (2026-07-30)** — 개인 가사 서버 연동(로컬 캐시 → 서버 → 제공자 검색 3단) + 재생 소스 진단 UX. 이전 0.16.0(선호 음악 앱·설정 [소스] 탭)·0.15.0("API 번역 꺼짐" 표기) 포함.
> 재개 방법: "이어서"라고 입력하면 아래 백로그부터 진행.

## v0.17.0 추가분 (Android 0.4.0 동반)
- **개인 가사 서버 연동(앱 쪽)** — 설정에 서버 주소·토큰을 넣으면 **로컬 캐시 → 서버 → 제공자 검색** 3단으로 조회하고, 새로 찾은 가사(번역 포함)를 서버에 올린다. 다른 기기가 이미 찾아 둔 곡은 검색·번역 없이 즉시 뜬다. 코어 `IRemoteLyricsCache`/`HttpRemoteLyricsCache` + `LyricsCoordinator.RemoteCache`, 조회 2.5초 타임아웃 + 연속 2회 실패 시 60초 서킷 브레이커 + 모든 실패를 null로 강등 → **주소를 넣지 않거나 서버가 꺼져 있으면 동작이 이전과 같다**. 서버 히트분은 로컬 캐시로 승격돼 다음부터는 오프라인에서도 뜬다.
  - Windows: 설정 [소스] 탭(토큰은 DPAPI 암호화) / Android: 설정 [재생 소스] 탭. 둘 다 저장 즉시 반영. 사용법은 `docs/lyrics-server-guide.md`.
- **재생 소스 진단 UX(Windows)** — 왜 플레이어가 안 잡히는지 화면에서 알 수 있게. ① 특정 앱으로 고정해 뒀는데 그 앱이 SMTC에 없으면 "재생 중인 곡 없음" 대신 "{앱} 대기 중 — 고정해 둔 플레이어가 실행 중이 아닙니다"로 표시 ② 설정 [소스]의 선호 음악 앱에 감지 상태 표시(● 지금 재생 중 / 지금은 감지 안 됨) ③ 트레이 재생 소스 목록에 선호·고정 앱 중 미감지 항목도 흐리게 표시.
  - 계기: Store 버전 MusicBee가 SMTC를 발행하지 않아 인식되지 않았고(원인은 앱 밖), 동시에 소스가 Spotify로 고정돼 있었다. 진단 방법은 `docs/lyrics-server-guide.md`의 "잘 안 될 때" 절.

## 미배포 (다음 앱 릴리스 후보 — Android 0.5.0)
- **잠금화면 가사(Android)** — 플로팅 오버레이는 잠금화면 위에 뜰 수 **없다**(Android 8부터 `TYPE_APPLICATION_OVERLAY`가 키가드보다 아래 레이어로 고정, `TYPE_SYSTEM_OVERLAY`는 일반 앱에서 제거 — 오버레이 권한 예외 없음). 대신 이미 `Visibility.Public`인 포그라운드 알림에 현재 줄을 실어 잠금화면에서 읽히게 한다: 접힌 알림 = 원문, 펼친 알림 = **원문 + 번역**. 설정 [오버레이] 탭의 "알림에 현재 가사 표시"로 끌 수 있다(기본 켬 — 끄면 듣는 내용이 잠금화면에 남지 않는다). 갱신은 최소 400ms 간격으로 묶는다(시스템의 알림 갱신 빈도 제한 — 간주 표시 줄처럼 짧은 줄이 연달아 나오면 갱신이 통째로 버려진다). 카라오케·색은 알림에서 불가능.
- **설정 [정보] 탭(Android)** — Windows [정보] 탭과 같은 내용(앱 이름·버전·구 LyricsX·소개·MPL-2.0·LyricsKit 출처·링크)에 안드로이드 전용 줄을 더했다: 패키지명·OS·기기를 보여 주고 **탭하면 버전까지 붙여 클립보드로 복사**(문제 신고용). 버전은 `PackageManager.GetPackageInfo`에서 읽어 릴리스마다 손댈 곳이 없다. 탭 이름은 5개가 한 줄에 들어가도록 줄였다(소스/번역/오버레이/광고/정보).
- **같은 곡 중복 조회 제거(코어)** — 가사 서버 로그에 s26이 같은 곡을 1~8초 간격으로 두 번 묻는 사례가 있었다. 원인은 `TrackInfo`가 record라 **길이·앨범이 뒤늦게 채워지면 값이 달라져** `TrackChanged`가 한 번 더 발화하고, 코디네이터가 그때마다 검색 파이프라인을 다시 돌린 것. 이제 `LyricsCoordinator`가 마지막으로 검색한 곡의 `제목|아티스트`를 기억해 같으면 건너뛰고 표시 상태만 갱신한다(`_searchedTrackKey`). Windows(SMTC)도 같은 형태의 재발화가 있어 함께 이득.
- **버블 두 줄 표기 = "켜 둠", 실제 표시와 분리(Android)** — 두 줄 표기가 실제 표시 여부(`_bandVisible`)를 따르고 있어, 가사가 없는 구간·일시정지에 한 줄로 돌아가 "꺼진 건가?" 싶게 만들었다. 이제 사용자가 켜 둔 상태(`_bandExpanded`)를 따른다. 그러려면 그 값을 아무도 흔들지 않아야 해서 peek을 별도 플래그(`_peeking`)로 분리하고, 이동 모드는 `_bandExpanded`를 건드리지 않게 했다(`UpdateVisibility`가 `_moveMode`만 봐도 밴드를 띄운다). 실제 표시 여부는 접근성 문구에만 쓴다.
- **버블 상태 표기 개선(Android)** — 버블 하나에 세 가지를 서로 다른 채널로 싣는다. 예전에는 테두리 색 하나에 번역 상태만 실려 있어 "가사를 찾는 중인지", "오버레이가 켜져 있는지"를 알 수 없었다.
  - **링 색 = 가사 상태**: 검색 중이면 흐린 트랙 위로 악센트 호가 회전(플레이스토어 다운로드 링과 같은 감각, 1.1초/바퀴), 찾았으면 밝게, 못 찾았으면 회색. 음표 글리프도 같이 흰색/회색.
  - **링 줄 수 = 오버레이가 지금 보이는가**: 보이면 같은 굵기·밝기(2dp·70%)의 링이 안쪽에 하나 더 생겨 두 줄, 안 보이면 한 줄(투명도 0.75/1.0만 바꾸던 이전 방식은 배경 위에서 구분이 안 됐다). 굵기·밝기는 상태에 따라 바꾸지 않는다 — 활성 쪽 선이 도드라져 화면을 방해한다. 근거는 `_bandExpanded`가 아니라 실제 표시 여부 — 펼쳐 뒀어도 일시정지·무가사면 밴드는 안 보인다.
  - **펼침/접힘은 사용자가 정한 상태**로 고정: 곡이 바뀔 때 접던 동작(0.3.0부터 있던 것, 이번 변경과 무관)을 없앴다 — 곡이 넘어갈 때마다 버블을 다시 눌러야 했다. 이동 모드는 강제로 펼치되 끝나면 들어가기 전 상태로 되돌리고, 설정 저장(`ActionRefreshDisplay`)도 접지 않는다(접는 건 버블 모드로 막 들어왔을 때 한 번뿐).
  - **안쪽 채움은 상태를 나타내지 않는다** — 오버레이 배경 설정(색·불투명도)을 그대로 따라 밴드와 같은 재질로 보이게 하고, 배경을 꺼 뒀으면 검정 45%. 첫 구현은 "표시 중 = 링 색으로 채움"이었는데 흰색으로 꽉 차 뒤 화면이 가려졌다 — 채움은 가림과 직결되므로 상태 채널로 쓰지 않는다.
  - **우상단 점 = 번역 예외**(한도·실패 주황 / API 꺼짐 회색 / 정상은 점 없음) — 링에서 밀려난 번역 상태를 잃지 않게.
  - 회전은 `ValueAnimator` 대신 핸들러 틱(16ms) — 개발자 옵션에서 애니메이션 배율 0인 기기에서 스피너가 멈추는 것을 피한다. 검색 중에만 돌고 버블을 뗄 때 `Stop()`. 색은 사용자 팔레트(`OverlayTextColor`/`OverlayKaraokeColor`)를 따르고 `ContentDescription`으로 TalkBack도 읽는다. `Views/BubbleStatusDrawable.cs`.
- **번역 공유 구멍 막기(코어 — Windows·Android 공통 + 서버)** — "한 기기가 번역하면 다른 기기는 다시 번역하지 않는다"가 실제로는 **새로 검색한 곡에만** 성립하고 있었다. 두 군데를 고쳤다.
  - 클라이언트: 업로드 지점이 "제공자 검색 직후"뿐이라, 저장 당시 번역이 없던 곡(API 꺼짐·한도 초과)이나 서버에 없는 대상 언어는 기기마다 각자 번역하고 그 결과가 어디에도 남지 않았다. 이제 **로컬 캐시·서버 히트 뒤 보충 번역이 실제로 채워지면** 로컬 캐시에 다시 저장하고 서버에도 올린다(`LyricsCoordinator.TranslateAsync(persistAfter:)` → `PersistTranslated`). 이미 다 번역돼 있으면(`changed == 0`) 올리지 않아 재생마다 `revision`이 늘지 않는다. 서버 히트분을 번역 **전에** 로컬 캐시로 승격하던 것도 함께 해소.
  - 서버: 조회는 느슨한 키까지 보는데 `Upsert`는 정확 키만 봐서, 같은 곡이 기기별 표기로 갈려 저장됐다("MGMT" vs "MGMT — Oracular Spectacular"). 이제 저장도 조회와 **같은 규칙**(`Locate`)으로 대상 행을 찾아 갱신하되, `key`·제목·아티스트는 먼저 저장된 표기를 유지한다(바꾸면 원래 기기의 정확 키 조회가 깨진다). 재업로드가 잦아지는 만큼 이 비대칭을 같이 막아야 중복 행이 늘지 않는다.
  - 계약(`contracts/lyrics-api.md`)에 "클라이언트의 PUT 시점" 절과 저장 쪽 키 매칭 규칙 추가. 테스트 7건(`TranslationSharingTests`, `LyricsStoreMergeTests` — 임시 SQLite 파일로 저장 경로까지).
  - **서버 재배포 필요**(앱 릴리스와 별개). 앱 쪽은 다음 릴리스에 포함.
- **Spotify 광고 자동 뮤트(Android, 옵인)** — 설정 [광고 뮤트] 탭에서 켜면 Spotify 광고 구간에만 미디어 볼륨을 0으로 내리고 곡이 돌아오면 되돌린다. 기본 꺼짐.
  - 감지는 Windows보다 깔끔하다 — 안드로이드에는 **표준 광고 플래그**(`android.media.metadata.ADVERTISEMENT`)가 있어 Spotify가 설정한다. `spotify:ad` 미디어 ID, 그리고 Windows에서 실측 검증된 `artist=Spotify`+빈 앨범을 폴백으로 겹친다(`Services/AdDecision.cs`의 `AdSignals`). 현지화 문자열 추측이 필요 없다.
    - 주의: **.NET for Android 바인딩에 `MetadataKeyAdvertisement` 상수가 없어** 플랫폼 키 문자열을 직접 쓴다(`Microsoft.Android.Ref.34`의 `Android.Media.MediaMetadata`에 `MetadataKeyMediaId`는 있는데 이건 없음 — 직접 확인).
  - **앱별 음소거가 아니다** — 안드로이드에는 다른 앱 볼륨만 조절하는 공개 API가 없어 기기 전체 미디어 볼륨이 내려간다(`AudioManager.SetStreamVolume(Stream.Music, 0)`). 설정 화면에 이 점을 명시했다. 같은 이유로 **MP3 채우기(Windows 기능)는 제외** — 같은 스트림이라 함께 죽는다.
  - 안전장치: 진입 디바운스 150ms(곡 전환 시 빈 메타데이터 오인 방지, 이탈은 즉시) / 최대 뮤트 180초(실측 광고 55~58초) / **볼륨을 남기지 않기** — 원래 볼륨을 `SharedPreferences`에 즉시 기록해 프로세스가 광고 도중 죽어도 다음 실행이 복구한다 / 뮤트 중 사용자가 볼륨 키를 누르면 그 광고는 포기(단 다음 광고 세그먼트에서는 다시 뮤트).
  - **연속 광고 사이에도 뮤트를 유지한다** — Spotify 광고는 보통 2개 연속인데 그 사이 재생이 잠깐 끊긴다. 이를 광고 종료로 보면 볼륨이 1.3초 돌아왔다 내려가 광고 소리가 샌다. 신호를 3상태(`AdSignal.Ad/NotAd/Unknown`)로 두고 재생이 멈춘 동안은 판정을 유지하되 5초(`AdDecision.PauseHold`)로 제한한다 — 광고 중 일시정지하고 자리를 떠도 볼륨이 0으로 남지 않는다. `1/2`·`2/2` 카운터 파싱은 쓰지 않았다(지역·표기 의존). 실측: 광고 구간 50초 내내 `vol=0`, `볼륨 복구` 로그 1회.
  - 포그라운드 서비스를 새로 만들지 않는다 — 알림 접근이 켜져 있으면 시스템이 `MediaListenerService`를 계속 바인드하므로 `MusebaseApp`이 컨트롤러를 들고 있으면 된다. 알림바 항목이 늘지 않는다.
  - 코어/엔진 무변경(골든룰) — 광고 플래그는 `Engine.TrackInfo`가 아니라 `AndroidNowPlayingSource`의 Android 전용 속성으로 노출. 배경은 `docs/adr/0006-android-ad-mute.md`.
  - **실기기 검증(SM-S947N / Android 16 / Spotify 9.1.68.1888 / 무료 계정)** — 광고 2연속 구간에서 `flag=1` + `mediaId='spotify:ad:…'` 확인, 뮤트·복구 정상. 강제 종료 후 재실행 시 볼륨 자동 복구 확인. **아티스트가 `'광고 • 1/2'`라 Windows에서 가져온 폴백 ③(`artist=Spotify`)은 매칭되지 않는다** — 신호 ①②가 잡으므로 문제없지만 폴백만 믿는 설계였으면 조용히 실패했을 것.
  - **실기기가 결함 4개를 잡았다**(코드 리뷰로는 안 보이는 것들 — 자세한 로그·표는 Android README):
    ① `IsPlayingChanged` 미구독 — 판정이 `IsAdvertisement && IsPlaying`인데 재생 변화에 반응하지 않아 연속 광고 사이 1초 지연
    ② 디바운스 만료 시점 재평가 미예약 — 150ms 설계값이 실질 1초. ①②로 신호→뮤트 지연이 **1.8초 → 0.24~0.39초**
    ③ `MediaVolumeMuter.Reapply()`가 이름과 반대로 "재적용"이 아니라 "포기"를 하고 있었다 → `CheckUserOverride()`로 개명
    ④ **볼륨 개입 포기가 광고 세그먼트 단위였다** — 사용자가 볼륨을 올려도 18초 뒤 광고 2/2에서 다시 내리눌렀다. 이제 실제 음악이 돌아올 때까지 유지한다(`raw==false`로 풀면 광고 사이 1.3초 공백에서 풀려 버린다)
    ⑤ **로그가 하지 않은 일을 했다고 찍었다** — 사용자 개입 구간이라 볼륨을 안 내렸는데도 "미디어 볼륨 0"을 남겼다. 삼성에서는 로그가 유일한 프로브라 치명적이다. `Mute()`/`Unmute()`가 실제 변경 여부를 `bool`로 돌려주고 호출자가 그에 맞춰 찍는다
    ⑥ **일시정지 보류와 세그먼트별 재뮤트가 서로 간섭했다** — 보류를 넣자 세그먼트 사이에 `Unmute()`가 안 불리는데 `_muted`를 내리는 곳이 거기뿐이라, 사용자가 볼륨을 올려 포기한 뒤 재뮤트가 막히고 다음 틱이 또 포기했다. 포기 시 `_muted`도 내린다. 겸해서 소스 이벤트를 `IsAdvertisementChanged` → `AdvertisementChanged`(여부 **또는 식별자** 변화)로 바꿔 1/2 → 2/2 전환을 즉시 알린다
  - **삼성 One UI는 서드파티 앱 로그를 막는다** — `adb shell setprop log.tag.Musebase VERBOSE` 없이는 `Musebase` 태그가 logcat에 한 줄도 안 나와 앱이 죽은 것처럼 보인다. `dumpsys media_session`은 metadata를 제목/아티스트/앨범 3개로만 덤프해서 광고 플래그가 안 보이므로, 앱이 찍는 `ad-signals` 로그가 사실상 유일한 프로브다.

## 미배포 (서버 쪽 작업 — 앱 릴리스와 무관하게 이미 운영 중)
- **가사 서버 백업 강화 + 컨테이너화** — 앱에는 영향 없음(서버 운영용).
  - 백업: `sqlite3 .backup` → **`PRAGMA integrity_check` 검증**(깨지면 폐기) → gzip(2MB→660KB) → 보존 정리. `MUSEBASE_BACKUP_REMOTE`를 넣으면 매일 오프사이트 사본까지. 복구 절차 문서화.
  - `Dockerfile`(멀티스테이지, tzdata·sqlite3·curl 포함, 비루트 실행, `/data` 볼륨, HEALTHCHECK) + `docker-compose.yml`(루프백 바인딩, 이름 있는 볼륨). WSL 도커에서 빌드·기동·백업·재시작 지속성·헬스체크까지 실측(이미지 372MB).
  - `deploy/MIGRATION.md` — 미니 서버로 옮기는 절차(토큰 유지하면 앱 설정 그대로), 백업/복구, Docker 전환 시 주의(볼륨 권한 1654, 루프백 바인딩, tailscale serve가 노출 전담).
- **가사 서버 관리자 페이지(v1.1)** — 브라우저로 여는 대시보드 + 가사 검색·열람·편집·삭제. 앱에는 영향 없음(서버 전용).
  - **조회 기록** `lookups` 테이블 신설(곡·결과 exact/cleaned/miss·기기·시각, 90일 자동 정리, `MUSEBASE_LOG_LOOKUPS=0`으로 끔). `PRAGMA user_version` 마이그레이션 도입.
  - **대시보드**: 마지막 조회/오늘/7일 히트율/보관 곡 수 타일, 최근 조회 50건, 미스 상위(채울 후보), 기기별·일별, 최근 업로드, 번역 없는 곡, 표기 차이로 갈린 곡 후보, 느슨한 매치 목록, 진단(헤더 원값·서버 상태).
  - **검색·열람**: 제목·아티스트 LIKE 검색 → 상세에서 원문·번역 3열 표(타임태그 토글·언어 선택·raw 다운로드) → **편집**(`origin=user`로 저장돼 자동 검색이 못 덮음)·**삭제**.
  - 인증은 서명 쿠키(`?token=`으로 부트스트랩 후 주소창 정리), 편집·삭제는 CSRF 토큰. JS 0줄이라 CSP를 `default-src 'none'`으로 잠금. 기기 이름은 테일넷 IP 매핑(`MUSEBASE_DEVICES`)으로 — 앱·코어 변경 없음.
- **개인 가사 서버 v1** — 앱에 서버 주소를 넣으면 **로컬 캐시 → 서버 → 제공자 검색** 3단으로 조회하고, 새로 찾은 가사(번역 포함)를 서버에 올려 다른 기기가 재검색·재번역하지 않는다. 가사 캐시가 이미 번역이 박힌 확장 LRC라 **곡 단위 조회/저장만으로 번역 공유까지 달성**된다.
  - 서버: `src/Musebase.Server`(ASP.NET Core, Musebase.Core 재사용 — 키 정규화를 클라이언트와 **같은 코드**로 계산). SQLite에 LRC를 **무손실 보관**, 병합 정책(사용자 편집본 보호 / 카라오케·번역 퇴화 거부 / 그 외 나중 것 우선), Bearer 토큰, `--import`로 기존 `translations.db` 시드.
  - 키 매칭: 정확 키 → 느슨한 키(피처링·리마스터 표기 제거 + **아티스트 뒤 앨범 꼬리 제거**). 실제 캐시 확인 결과 Windows SMTC는 아티스트를 "MGMT — Oracular Spectacular"로 보고하는데 Android는 "MGMT"만 보고해, 이 처리가 없으면 기기 간 히트가 갈린다.
  - 클라이언트: `IRemoteLyricsCache`/`HttpRemoteLyricsCache`(코어) + `LyricsCoordinator.RemoteCache`. 조회 2.5초 타임아웃 + 연속 2회 실패 시 60초 서킷 브레이커 + 모든 실패를 null로 강등 → 서버가 없거나 테일넷 밖이어도 동작이 이전과 같다. 서버 히트분은 로컬 캐시로 승격.
  - 배포: Oracle(ARM64) + systemd + `tailscale serve` HTTPS(공개 포트 0, Android 평문 차단 회피). 절차는 `src/Musebase.Server/deploy/README.md`.
  - 문서: `contracts/lyrics-api.md`(v1), `docs/adr/0005-personal-lyrics-server.md`. 설정은 Windows [소스] 탭 / Android [재생 소스] 탭, 저장 즉시 반영.

## v0.16.0 추가분 (Android 0.3.0 동반)
- **Windows: 설정 [소스] 탭 분리** — [일반]에 있던 "선호 음악 앱(재생 소스)"과 "가사 소스"를 새 [소스] 탭으로 옮겼다(일반에는 언어·미니창·브라우저 디스플레이·텔레메트리만 남는다).
- **Windows: 선호 음악 앱 + 비음악 앱 자동 제외** — 설정 [일반]에 "선호 음악 앱(재생 소스)" 체크 목록 추가(고르면 그 앱들만 인식, 비우면 자동). 자동 모드의 제외 대상에 브라우저뿐 아니라 **영상·팟캐스트 앱**(YouTube·Netflix·Wavve·TVING·Podcast 계열 등)을 더했다 — YouTube Music은 음악이라 예외. 저장 키 `preferredSources`(SourceAppUserModelId 목록), Android `PreferredSources`와 같은 의미.
- **번역 상태 표기 세분화** — API 번역을 꺼도 캐시로 전부 채워졌으면 "캐시 이용 (API 꺼짐)"으로 구분 표시(코어 `TranslationDisplayStatus.DisabledCached`). 이전에는 번역이 정상 표시되는 상황에서도 "API 번역 꺼짐"만 보여 번역이 안 되는 것처럼 읽혔다. 판정은 `apiNeeded = LinesNeeded - CacheHits`가 남았는지로 한다.
- **i18n 보강** — 17개 로케일에 `translation.status.*`(7종 + 신규 `disabledCache`)와 `tray.apiTranslation`(+툴팁)을 채웠다(그동안 en 폴백). 로케일당 89→99키. 남은 76키는 백로그.
- **빈 구간에 배경판만 뜨던 문제** — LRC의 간주 표시 줄은 시간 태그 뒤 내용이 공백뿐인데(`Lyrics.Parse`는 트림하지 않는다) 표시 판정이 `IsNullOrEmpty`라 통과해, 글자 없는 배경판만 남았다(Windows=가로 띠, Android=작은 사각형). 원문·번역 모두 공백 기준(`IsNullOrWhiteSpace`)으로 바꿔 그 구간을 숨긴다 — 양 플랫폼 동일 규칙. 이동 모드에서는 종전대로 표시.
- (Android 병행) **오버레이 표시 방식** — 시스템 바 인셋 회피 기본 위치, 이동 모드 드래그(위치 비율 저장·회전 대응), 버블(플로팅) 모드(탭 펼치기/접기, **길게 누르면 퀵 메뉴** — 앱 열기·API 번역 토글(상태 표시)·위치 이동, 끌면 하단 **포켓(✕)** 에 놓아 오버레이 끄기, 가장자리 자석, 상태별 테두리 색, peek). **알림 터치 → 앱 화면**. 퀵 메뉴·앱 화면에서 **완전 종료**(오버레이 중지 + 감지 중지 + 알림 리스너 언바인드 → 프로세스 회수, 앱 재실행 시 자동 복구). **선호 음악 앱 다중 선택**(고르면 그 앱들만 소스로 인정 — 팟캐스트·영상 차단, 비우면 자동). **메인 화면 개편**: 전체 가사 스크롤(현재 줄 강조·자동 가운데 정렬) + 재생 컨트롤 + 나머지는 우상단 아이콘 바·권한 배너. 앱 내 가사 번역도 **오버레이와 같은 규칙**으로 골라 제공자 중국어 번역이 뜨지 않는다. **설정 화면을 기능별 탭(재생 소스/번역/오버레이)으로 분리**하고 **오버레이 스타일**(글자 크기·색 3종·배경 표시/색/불투명도·모서리·페이드·글자단위 카라오케·대상 언어만 표시)을 Windows와 같은 항목으로 추가. 오버레이 **페이드 인/아웃**. **번역 완료 시 앱 내 가사 즉시 교체**(같은 곡이면 뷰를 다시 만들지 않고 번역 줄만 갱신 — 스크롤·강조 유지). 설정 색 항목에 **팔레트 대화상자**(견본 24색 + 직접 입력). **가사 검색·선택 화면**(제공자 동시 검색 → 품질 순 후보 → 미리보기 → 적용, Windows 검색 창과 같은 흐름)과 **틀린 가사로 표시**(확인 후 `MarkWrongLyrics`)를 아이콘 바에 추가. 알림 액션에 "위치 이동" 추가. 곡이 바뀌면 이전 곡의 마지막 줄이 남지 않도록 카드를 비운다. **세로 모드에서는 밴드를 가로 가운데 고정**(드래그는 높이만, 회전 시 `KaraokeTextView.SetMaxWidth`로 폭 재계산).

## v0.15.0 추가분
- **"API 번역 꺼짐" 표기** — 트레이 토글로 API 번역을 끄면 "· 번역: API 번역 꺼짐"으로 표시(이전에는 "캐시 이용"과 구분되지 않았다). 코어 `TranslationDisplayStatus.Disabled` 추가, 꺼진 상태에서는 "번역 중"으로 깜빡이지 않는다.
- **트레이 메뉴 줄바꿈** — 곡명·검색 상태가 길면 트레이 메뉴가 화면 폭만큼 늘어나던 것을 최대 폭 360 DIP + 자동 줄바꿈으로 수정.
- (Android 병행: 알림바에 곡명·가사/번역 상태 표시 + "번역 끄기/켜기" 액션, 재생 소스 선택(영상·브라우저 앱 기본 제외 — YouTube 오인식 방지), Android 13+ 알림 권한 런타임 요청 — PR #17·#18)

## v0.14.0 추가분
- **Google Cloud Translation 엔진** — Translation API v2(API 키). 원문 자동 감지, DeepL식 대상 언어 코드를 Google 코드로 변환(EN-US→en, ZH-HANT→zh-TW 등), 응답 HTML 엔티티 디코드. 레지스트리 한 줄 등록으로 Windows·Android 설정에 함께 노출.
- **엔진별 API 키 입력** — 설정 [번역] 탭을 엔진 → 그 엔진의 키 → 엔드포인트 → 대상 언어 순으로 재배치. 키 입력란이 선택한 엔진을 따라가고, 엔진을 바꿔도 입력값은 엔진별로 보관. DeepL/Google/LibreTranslate 키 모두 DPAPI 암호화 저장(기존 `deeplApiKeyEnc`·entropy 불변).
- **트레이 "API 번역 사용" 토글** — 유료 API 사용량을 즉시 끊는 스위치. 끄면 새 번역 요청 없이 캐시된 번역만 표시(코어 `LyricsTranslationService.CacheOnly`), 다시 켜면 재생 중인 곡부터 즉시 재번역(`LyricsCoordinator.RetranslateCurrentAsync`).
- 폴백 옵션 문구 일반화: "DeepL 실패 시" → "선택한 번역 엔진 실패 시"(설정 키는 유지).
- (Android 병행: Google 엔진·엔진별 키·폴백/API 사용 체크박스 — PR #15, sln 밖)

## v0.13.0 추가분
- **번역 상태 표기** — 가사 소스/품질 옆에 "· 번역: 정상 번역 / 캐시 이용 / 한도 초과"를 표시(트레이 툴팁·미니창). 무료 번역(MyMemory) 한도 초과 등 실패 여부를 바로 확인. 코어 `LyricsCoordinator.CurrentTranslationStatus`로 계산해 PC·Android 공유.
- (Android 병행: 떠 있는 가사 오버레이+카라오케, 번역 설정 화면 — PR #13, sln 밖)

## v0.12.0 추가분
- **브라우저 디스플레이** — 트레이 "브라우저 디스플레이" 토글로 인프로세스 서버(Musebase.Browser) 기동, 같은 LAN의 태블릿·TV·폰 브라우저에서 `http://<PC IP>:5123` 접속 시 실시간 가사(카라오케 채움·번역 포함) 표시. 설정에 포트·LAN 접속 허용, 켤 때 접속 URL 안내. LAN 접속은 Windows 방화벽 인바운드 TCP 허용 필요. (Phase 1 완료 — PlaybackViewState 계약 실사용 검증)

## v0.11.0 추가분
- **무키 번역 기본 MyMemory 전환** — LibreTranslate.com이 API 키 필수로 바뀌어 무키 기본이 깨져 있던 것을 수정. MyMemory(공식 무료·자동감지) 기본. DeepL 할당량 초과 등 실패 시 무료 엔진 자동 폴백 옵션 + 실패 안내(로그·힌트).
- **설정창 4탭**(일반/번역/오버레이 스타일/정보) + 긴 한글 줄바꿈/잘림 수정.
- **작업표시줄 미니창(컨트롤 허브)** — 곡정보·가사소스·재생컨트롤·오프셋·검색·가사열기·틀린가사를 트레이/오버레이 없이 사용. 닫기→트레이 옵션.
- 오버레이 배경 라운드 + 미디어컨트롤 좌상단, 기본값(노래방색 #FFEB3B·배경 불투명도 25%), 앱 아이콘 L→M.
- 멀티플랫폼: Browser 인프로세스 호스팅 서버(`BrowserDisplayServer`), Android 가사 엔진 조립(실기기 검증).

## v0.10.0 추가분 (첫 Musebase 릴리스)
- **옵트인 텔레메트리(ADR-0004)**: 익명 랜덤 GUID, 2단계 동의(①기본/②품질 — 다이얼로그·설정 토글, 기본 꺼짐), Engine `ITelemetry` 계측(lyrics_search/translation/wrong_lyrics/…), Windows `TelemetryClient`(JSONL 큐→시작 30초+1시간 주기, 틀린가사 즉시 업로드), 백엔드 Cloudflare Workers+D1(`backend/telemetry/`, /stats 공개·/admin 토큰 보호). 공개 문서 `TELEMETRY.md`, 계약 `contracts/telemetry-events.md`.
- **Phase 1·2 스파이크**: `src/Musebase.Browser`(PlaybackViewState WS 방송+웹 카라오케 렌더러, --demo) · `src/Musebase.Android`(MediaSession 재생감지, 실기기 검증, sln 미등록).
- 릴리스 태그 스킴 전환: 플랫폼 접두 **`windows-vX.Y.Z`**(ADR-0003).

## Phase 0 (개명 + 거버넌스, 2026-07-16)
- **개명 LyricsX→Musebase**: 프로젝트 `Musebase.{Core,Engine,Windows}`(구 App→Windows)·`Musebase.sln`·네임스페이스·AssemblyName(`Musebase.exe`) 일괄. `%LOCALAPPDATA%\LyricsX`→`Musebase` 자동 이전(`MigrateLegacyAppData`), DPAPI entropy는 호환 위해 `"LyricsX.DeepL.v1"` 유지, 시작프로그램 레지스트리 값 `Musebase`(+구 값 정리). Velopack packId `Musebase` = 구 설치본 자동 업데이트 단절(클린 브레이크, RELEASING.md 참고).
- **LICENSE(MPL-2.0) + 출처 표기**: 원본 LyricsX/LyricsKit(ddddxxx, MPL-2.0) 기반 명시. README 라이선스 절의 GPLv3 오기 수정.
- **거버넌스**: CI 게이트(ci.yml), 루트 CLAUDE.md(소유권 지도+골든룰), .claude/agents/*, CODEOWNERS, contracts/playback-view-state.md, ADR-0003.

## v0.9.2 추가분 (재생 소스 + 미디어 컨트롤 + 엔진 리팩터)
- **재생 소스 선택** — 트레이 "재생 소스" 서브메뉴(자동/특정 플레이어)와 설정. 자동 모드는 브라우저(SMTC) 세션 제외(`BrowserTokens`)로 Firefox/YouTube 오인식 해결, 특정 소스 잠금 시 해당 앱 세션만 사용. `NowPlayingService.PickBestSession` 재작성 + `[smtc] 세션 목록:` 진단 로그.
- **정지 시 오버레이 완전 숨김** — 가사뿐 아니라 배경판까지 숨김.
- **오버레이 미디어 컨트롤** — 마우스오버 시 좌측에 이전/재생·일시정지/다음 버튼(`MediaControlWindow`), SMTC `PlaybackControls` 반영.
- **엔진 리팩터(steps 1–6)** — UI 무관 `LyricsX.Engine` 신설: `INowPlayingSource`/`IEngineDispatcher`/`LyricsStatus`/`PlaybackViewState`(직렬화 표시계약)/`PlaybackViewModel`/`ISecretStore`/`EngineConfig`+`LyricsEngineFactory`. 멀티플랫폼(Android/브라우저) 재사용 기반. ADR 0001(코어 언어 .NET 유지)/0002(플러그형 소스·번역).
- **소스/번역 레지스트리** — `LyricsSourceRegistry`(LRCLIB만 공식 API 표시)+`EnabledLyricsSources`, `TranslatorRegistry`+무키 무료 `LibreTranslateTranslator`(DeepL 키 없으면 기본). 설정창 [일반] 탭에 소스 체크박스·번역 엔진 콤보·엔드포인트 노출. Core 테스트 82개.

## v0.9.1 추가분 (번역 표시 정책)
- **"대상 언어 번역만 표시" 설정(기본 켬)** — 제공자(Kugou/QQ/NetEase)가 끼워 넣는 다른 언어 번역(주로 중국어)을 숨기고, DeepL 대상 언어 번역(`tr:{target}`)만 표시. **최초 설치·DeepL 키 없는 사용자는 원문만** 표시. `AppSettings.ShowOnlyTargetTranslation`, `LyricsCoordinator.ResolveDisplayTranslation`.
- **중국어(ZH) 예외** — 대상 언어가 ZH면 제공자 번역이 곧 중국어이므로 DeepL을 거치지 않고 제공자 `tr`을 그대로 표시(`TargetIsChinese` → `TranslateAsync` 스킵 + 표시 우선). ZH 사용자는 키 없이도 중국어 번역 표시.
- 우려/한계는 세션 기록 참조(ZH+제공자번역 없는 곡은 원문만, 제공자 tr 언어 태그 부재 등).

## v0.9.0 요약 (다국어 + 보안 + 설정 UI)
- **UI 다국어 19개어**(en 참조 + ko 손번역 + DeepL 시드 17), 시스템 언어 기본·영어 폴백, 설정 언어 선택기 + GitHub 번역 기여 링크. 최초 실행 시 표시언어·번역대상언어를 시스템 언어로 기본 선택.
- **DeepL 키 보안**: settings.json에 DPAPI 암호화 저장(구 평문 자동 마이그레이션) + 설정창 PasswordBox 마스킹·눈토글.
- **설정창 개편**: 탭 2개(일반/오버레이 스타일), 긴 문구 줄바꿈, 콤보·슬라이더 폭 조정, 세로 스크롤 제거.
- 상세는 아래 각 절 참조.

## 보안: DeepL 키 보호
- **저장 암호화**: settings.json에 평문 대신 **DPAPI(CurrentUser) 암호문**(`deeplApiKeyEnc`)만 저장. 구버전 평문 키(`deeplApiKey`)는 로드 시 자동 마이그레이션(다음 저장에서 암호화, 평문 필드 제거). `Services/Secret.cs`, `AppSettings`(JsonIgnore 평문 접근자 + WhenWritingNull). NuGet `System.Security.Cryptography.ProtectedData`.
- **화면 마스킹**: 설정창 API 키를 `PasswordBox`(점 표시)로 가리고 눈(👁) 토글로만 잠깐 평문 표시. `SettingsWindow`.
- 점검 결과: 전송은 HTTPS 헤더(안전), 로그·내보내기·git엔 키 미노출(안전). 검증: 마이그레이션→암호문 저장(평문 `:fx` 파일에서 사라짐)→재로드 복호화(마스킹 표시)까지 실측 확인.
- 한계: 동일 사용자·동일 PC 코드는 DPAPI 복호 가능(로컬 앱 비밀 한계). 설정 잠금(비밀번호)은 과함으로 미채택. **주의: 0.9.0 실행 시 기존 평문 키가 자동 암호화되어 구 0.8.0에선 키 인식 불가**(재입력 필요).

## 다국어(UI i18n) — 작업 중 (미배포)
프레임워크: **로케일별 JSON + ICU MessageFormat**, 참조어=영어(en), 기여=**GitHub 네이티브**(편집·PR/이슈). 자세한 제안·결정은 세션 기록 참조.
- **런타임 조회 서비스** `Services/Localization.cs`(`Loc.T(key, args)`) — 임베디드 JSON 카탈로그 로드, 문화권 폴백((설정·시스템)→정확/중립 매칭→en), ICU 인자/복수형, `CultureChanged` 이벤트. 설정 `AppSettings.UiLanguage`(기본 `"system"`), 시작 시 `Loc.Initialize`.
- **P1 완료: 전 UI 현지화** — 설정창·트레이 메뉴·검색창·편집창·오버레이 힌트·자물쇠 툴팁·업데이트/내보내기 MessageBox·코디네이터 상태문구까지 `Loc.T`로 치환(키 83개). 설정창에 **표시 언어 드롭다운 + "번역 개선하기…" Weblate 링크**. 언어 변경 시 설정창·트레이 즉시 재현지화(`CultureChanged`).
- **지원 언어 19종**: 기본 10(en, ko, ja, zh-Hans, zh-Hant, es, pt-BR, fr, de, ru) + OSS 활발 9(it, pl, tr, nl, uk, cs, vi, id, ar).
- **번역 기여 = GitHub 네이티브**(인프라 0, 승인 불필요): "번역 개선하기…" 링크(`Loc.ContributionUrl`)가 리포 `TRANSLATING.md`로 → i18n JSON 직접 편집·PR 또는 이슈 템플릿(`.github/ISSUE_TEMPLATE/translation.yml`)으로 제안. Hosted Weblate(libre)는 승인 대기라 보류(추후 Tolgee/Weblate 셀프호스팅·Hosted Weblate로 이관 가능).
- **MT 시드**: `tools/mt-bootstrap.ps1`(DeepL, **ICU 자리표시자 XML 태그 보호+검증**). 17개 언어 카탈로그를 DeepL로 시드 생성(자리표시자 무결성 100%, 검증 통과).
- 검증: 빌드 Debug/Release 클린, 유닛 73 통과, 카탈로그 19종 JSON·키정합·자리표시자 검증, ko/en/ja/de 런타임 로드 확인. **남은 것**: MT 시드 사람 검토, 릴리스 배포.
- 미현지화(데이터성, 의도적): 편집본 `ServiceName="사용자 편집"` 마커, `search.status.count` 복수형 내부어(MT는 영어 유지 → 기여자가 다듬음).

## v0.8.0 추가분 (오버레이 UX 옵션 5종)
1. **페이드 인/아웃** — 가사 줄이 바뀔 때 크로스페이드, 오버레이가 나타나고 사라질 때 창 불투명도 페이드(180ms). 설정 `FadeAnimation`(기본 켬). 진행 갱신(SetProgress)은 페이드 없이, 내용 변경 시에만 크로스페이드. `OverlayWindow.SetLine/ApplyLineContent/ShowOverlay/HideOverlay`
2. **오버레이 배경(반투명 판)** — 색+불투명도(0~1) 조절해 가사 뒤에 배경 판 표시. 설정 `OverlayBackgroundEnabled/Color/Opacity`(기본 끔, #000000, 0.4). 이동 모드의 어두운 배경과 공존(이동 모드 우선). `OverlayWindow.ComputeBackgroundBrush`
3. **Win10 Spotify 인식 수정** — `GetCurrentSession()`만 믿지 않고 전체 SMTC 세션 열거 후 '재생 중' 세션 우선 선택(Win10에서 Spotify가 current로 안 잡히던 문제). `SessionsChanged` 구독 + 250ms 폴링마다 재선택, 선택 바뀔 때만 재구독. `NowPlayingService.SelectBestSession/PickBestSession`
4. **자물쇠 아이콘 단순화** — 이모지(🔒/🔓) → 벡터 라인 스타일 자물쇠. 잠금=회색 닫힌 걸쇠, 해제(이동 모드)=녹색 열린 걸쇠로 상태를 모양+색으로 명확히 구분. `LockButtonWindow`(Path 기반)
5. **마우스 오버 시 숨김** — 오버레이 위에 커서를 올리면 가사·오버레이를 잠시 숨겨 화면 가림 방지. 설정 `HideOnMouseOver`(기본 끔). 숨긴 뒤에도 커서 이탈을 판정하도록 화면 영역(물리 px)을 캐시. 이동 모드 중에는 무시. `OverlayWindow.OnHoverTick/IsCursorOverOverlay`
- 빌드 Debug/Release 통과, 유닛 테스트 73 통과(App은 UI라 테스트 없음). 데모 모드 실행으로 배경·자물쇠 아이콘 시각 확인.

## v0.7.2 추가분
1. **틀린 가사 표시** (트레이 → "가사 없음으로 표시 (틀린 가사)") — macOS `wrongLyrics` 참고. 표시 중단 + 캐시 제거 + 해당 곡 재검색·표시 억제(설정에 영속). 수동 검색/편집 시 억제 해제. `LyricsCoordinator.MarkWrongLyrics`, `AppSettings.SuppressedTracks`
2. **자물쇠 버튼 흰색 배경** — 어두운 반투명 → 흰색 바탕+테두리로 가시성 향상 (`LockButtonWindow`)
3. **내보내기에 기계번역 포함** — `ToLegacyString(preferredLang)`로 화면과 동일하게 대상 언어(tr:{target}) 번역 우선 포함
4. **검색 창 자동 검색** — 트레이 "가사 검색" 클릭 시 현재 곡을 바로 검색(제목 있으면 열자마자 실행), 최고 품질 자동 선택
5. **검색 결과 미리보기** — 목록 우측 미리보기 창에 선택 항목의 원문+번역 표시(GridSplitter로 폭 조절)
- 신규 테스트 1종(내보내기 대상 언어 우선), 전체 72 통과

## v0.7.1 추가분
- **편집 창 "간편 보기" 토글** — 전체(확장 LRC 무손실) ↔ 간편(`[시간]원문【번역】`) 전환. 간편 보기에서 저장 시 원본에 병합해 **글자단위 노래방(tt)·다른 언어 번역 보존**
- **간편 보기 언어 콤보박스** — 가사에 존재하는 번역 언어(+generic "번역")를 골라 해당 언어만 인라인 표시·편집. `Core: LyricsEditing`(ToSimpleText/ApplySimpleEdit/TranslationTags, 순수 함수·단위테스트)
- **동일 번역 숨김 시 폰트 튐 수정** — "동일 번역 숨김"으로 번역 줄이 가려질 때 원문 폰트가 커지던 문제 수정. 번역이 **표시될 때와 같은 크기(h*0.34)** 유지(진짜 번역이 없는 곡은 기존대로 h*0.44)
- 신규 테스트 4종(TranslationTags/ToSimpleText/ApplySimpleEdit 보존·삭제), 전체 72 통과

## v0.7.0 추가분
- **현재 가사 편집** (트레이 → "현재 가사 편집…") — 내장 편집 창에서 확장 LRC(무손실)를 수정 → 저장 시 파싱 검증 후 캐시(`lyrics_cache`)에 반영 + 오버레이 즉시 갱신
  - 출처를 "사용자 편집"으로 저장, **기계번역 재적용 건너뜀**(사용자 번역 보존), 진행 중 검색이 덮어쓰지 않도록 취소
  - `[tt]` 글자단위 노래방 태그까지 라운드트립 보존(무손실). `LyricsCoordinator.SaveEditedLyrics`
- **가사 내보내기 (.lrc)** (트레이 → "가사 내보내기 (.lrc)…") — `Microsoft.Win32.SaveFileDialog`로 `아티스트 - 제목.lrc` 저장. 이중언어 형식(`[mm:ss.fff]원문【번역】`, 표준 플레이어 호환), UTF-8(BOM 없음)
  - 파일명 금지문자 자동 치환, 새 NuGet 의존성 없음
  - 두 메뉴는 재생 곡+가사가 있을 때만 활성(메뉴 열릴 때 갱신)
- 원본 macOS의 `showCurrentLyricsInFinder`(파일 열어 편집) 취지를, DB 저장소에 맞게 내장 편집 창으로 적응
- 신규 테스트 2종(라운드트립 무손실 / 이중언어 내보내기), 전체 68 통과

## v0.6.3 추가분 (버그·UX 수정 5종)
1. **정지 시 가사 잔류 수정** — SMTC PlaybackInfoChanged 이벤트 지연(특히 Spotify) 보완: 재생 상태를 250ms 주기로 폴링해 정지 즉시 오버레이 숨김
2. **색상 팔레트 선택** — 설정에서 색 미리보기 클릭 시 32색 팔레트 팝업 → 스와치 클릭으로 hex 선택(수동 입력도 유지)
3. **동일 번역 숨김 옵션** — 번역이 원문과 같으면 번역 줄을 숨기는 설정 추가(기본 켬)
4. **용어 변경** — 설정/앱 UI의 '카라오케' → '노래방'(직렬화 키·내부 식별자는 호환 위해 유지)
5. **Apple Music 진행 떨림 수정** — 위치 보간이 새 타임라인 갱신 때 뒤로 튀던 현상 완화: 같은 곡 재생 중 1초 미만 역행은 흡수(시킹 등 큰 변화는 그대로 반영)

## v0.6.2 추가분
- **QQ Music 실응답 버그 수정** — `lyric_download.fcg` 실제 응답은 `<content type="file" ...><![CDATA[HEX]]>` 형태였음. `ExtractElement`가 속성 있는 태그 매칭 + CDATA 언랩을 하도록 수정 → QQ가 실제로 글자단위(QRC) 가사 반환
- **실검색 통합 검증 완료** — 실제 API로 4개 제공자 전부 확인:
  - Kugou: 글자단위(KRC XOR+zlib 복호 실동작), NetEase: 글자단위+번역(yrc/klyric), QQ: 글자단위(QRC 3중 DES 복호 실동작), LRCLIB: 라인단위
  - 메타 정제 확장 실효과 확인("Bohemian Rhapsody - Remastered 2011" → 정제된 매치 다수)
  - `LiveSearchProbe`(env `LYRICSX_LIVE=1` 게이트) 추가 — 오프라인 CI 안전, 수동 실검증용
- **의의**: 오프라인으로 포팅한 3중 DES(비표준 S-Box 포함)가 실제 QQ QRC를 정확히 복호함을 입증

## v0.6.1 추가분
- **트랙 메타 정제 검색 확장** — `SearchTermCleaner`가 피처링/리마스터/라이브/버전 표기 등 잡음을 제거한 검색어 변형을 생성
  - `LyricsSearchService`가 원본 검색어와 정제 변형을 **동시에** 검색(순차 재시도 대비 지연 없음) + (제공자, 곡 토큰) 기준 중복 결과 제거
  - 제목만 정제(대시/괄호 잡음 + feat), 아티스트는 feat만 제거(`Simon & Garfunkel` 같은 다인 아티스트 보존)
  - `Spider-Man`처럼 공백 없는 대시는 보존
  - 신규 테스트 16종(전체 62 통과), 가짜 제공자로 확장·중복제거 검증
  - LyricsKit `LyricsSearchRequestPlugin`(검색 확장) 취지를 메타 정제로 구현

## v0.6.0 추가분
- **자동 업데이트** — Velopack 1.2.0 + GitHub Releases(`countnine/LyricsX-Windows`, 공개)
  - `UpdateService`(GithubSource, prerelease=false) + `Program.cs` 배선. 시작 시 백그라운드 확인(비침습), 트레이 "업데이트 확인…" 수동 확인
  - 개발/디버그 실행은 `IsInstalled=false`로 무동작. 설치본에서만 확인·적용·재시작
  - `VelopackApp.Build().Run()`을 `Main` 최상단에 배치(설치/업데이트/제거 훅)
  - csproj `<Version>0.6.0>`, 트레이 툴팁·메뉴에 버전 표시
  - 릴리스 절차 문서화: `RELEASING.md` (vpk pack → vpk upload github)
- **원격 저장소 연결** — `origin` = https://github.com/countnine/LyricsX-Windows (공개), master 추적

## v0.5.1 추가분
- **NetEase yrc/klyric 파싱** — `NetEaseLyricParser`(ParseYrc/ParseKLyric). FetchAsync가 yrc(신형 단어단위) → klyric(구형) → lrc 순으로 우선. 글자 단위 카라오케(v0.5.0)가 NetEase 곡에도 적용됨
  - yrc: `(absStartMs,durMs,0)fragment`, klyric: `(0,durMs)fragment[(0,1) ]`(지속시간 누적)
  - 인라인 파서 3종(Kugou/QQ/NetEase) 모두 `tt`를 `AttachmentTags`에 등록 → 품질 랭킹 `InlineTimeTagBonus` 적용(단어단위 가사 우대)
  - 신규 테스트 3종(전체 46 통과)

## v0.5.0 추가분
- **글자 단위 카라오케** — 인라인 타임태그(`tt`)가 있는 라인은 글자 위치까지 정확히 채움, 없으면 기존 라인 단위 폴백
  - `InlineTimeTags.CharIndexAt(time)` — 라인 상대 시각 → 소수 글자 인덱스(구간 선형보간), Core 순수 함수로 단위 테스트
  - `OutlinedTextElement` — 글자별 누적 x 오프셋(`BuildHighlightGeometry`)을 캐시해 소수 글자 위치를 픽셀로 변환·채움
  - `LineProgressChanged`가 0~1 비율 → **라인 시작 이후 경과(초)**로 변경 (글자/라인 단위 공용)
  - 설정 토글 `글자 단위 카라오케`(기본 켬) 추가 — 타이밍이 어긋나는 곡에서 끌 수 있음
  - 데이터 소스: Kugou/QQ(v0.4.0). NetEase yrc/klyric 파싱은 추후 연결 시 자동 적용

## v0.4.0 추가분
- **Kugou(酷狗) 제공자** — 검색(mobilecdn) → 후보(krcs) → KRC 다운로드 → XOR+zlib 복호 → 파싱. `[language:base64]` 헤더의 번역(type==1) 병합
- **QQ Music(QQ音乐) 제공자** — smartbox+musicu 병렬 검색 → lyric_download.fcg XML → QRC(3중 DES: ddes/des/ddes, ECB) 복호 → 파싱. contentts 번역 병합
- 두 제공자 모두 글자단위 인라인 타임태그(`tt`) 생성 → 품질 랭킹에서 +보너스 (백로그 2번 글자 카라오케의 데이터 소스)
- `LyricsSearchService` 기본 목록에 등록(LRCLIB/NetEase/Kugou/QQMusic) — 수동 검색·자동 표시 자동 연결
- 신규 유닛 테스트 8종: KRC/QRC 복호 라운드트립, DES 가역성, 파서, 중첩 XML 추출 (전체 40 통과)
- **주의**: 복호기·파서는 라운드트립 검증 완료. QQ의 네트워크/XML 응답 스키마는 오프라인 검증 불가 → 실제 응답으로 필드 튜닝 필요할 수 있음

## v0.3.0 추가분
- 일시정지/정지 중 오버레이 자동 숨김 (재생 재개 시 복원, 이동 모드 중엔 유지, --demo 제외)
- 오버레이 스타일 설정: 원문/카라오케/번역/외곽선 색(hex+미리보기) + 외곽선 두께 — 저장 즉시 반영
- v0.2.1: 이동 모드 드래그 후 자물쇠 재클릭 불능 수정 (자물쇠를 소유 창으로 — z-순서 보장)

## v0.2.0 추가분
- 전체화면 앱 감지 시 오버레이 자동 숨김 (`FullscreenDetector`, 1s 폴링, 이동 모드 중엔 억제 안 함)
- 호버 자물쇠 버튼 (`LockButtonWindow` 별도 클릭 가능 창 — 본체는 클릭스루라 직접 호버 불가) → 이동 모드 토글
- 이동 모드에서 가장자리 드래그로 크기 조절 (WM_NCHITTEST), 내부 드래그 = 이동, 종료 시 크기·위치 저장
- 텍스트 크기 = 오버레이 높이 비례 + 긴 줄 폭 맞춤 자동 축소. 설정의 폰트 슬라이더 제거

## 완성된 것 (전부 검증됨)
- **M0** 스파이크 3종 → 스택 확정 (WPF, SMTC 보간, 지오메트리 렌더)
- **M1** Core 엔진: LRC 파서 / LRCLIB·NetEase 제공자(EAPI 암호화) / 품질 랭킹·병렬 집계 — 32 유닛 테스트
- **M2** SMTC 재생 감지 + 스트리밍 검색(첫 결과 ~0.9s) + 트레이
- **M3** 오버레이: 이중언어 2단 + 카라오케 채움 + 클릭스루 + 이동 모드 — **사용자 실검증**
- **M4** DeepL 번역 폴백(tr:{target}→tr 체인, SQLite 라인 캐시) + 설정 창 — **사용자 실검증**
- **M5** 가사 캐시(<100ms 재표시·오프라인) / 자동 실행 토글 / .ico / 수동 검색 창 / 배포 패키징
- 배포: `artifacts\LyricsX-Windows-v0.1.0-win-x64.zip` (70MB, self-contained 단일 exe)

## 백로그 (다음 작업 후보, 우선순위 순)
1. **오버레이 실표시 검증** — 실제 재생 중 Kugou/QQ/NetEase 글자 카라오케가 오버레이에서 채워지는지 화면 확인(제공자 데이터는 실검증됨, 렌더 관찰만 남음)
2. **i18n 잔여 누락** — 로케일당 76키가 아직 en 폴백(`mini.*`, `about.*`, `settings.browserDisplay.*`, `telemetry.*`, `tray.source*`, `translate.fail.*` 등). 번역 상태·API 토글 키는 채움
3. 글자단위 외 UX 개선 여지 — 검색 결과 미리보기/수동 선택 UX, 오프셋 미세조정 등
4. **(보류) 캐시 공유 + 일괄 사전번역** — 기기 간 `translations.db` 병합, 자주 듣는 곡 일괄 사전번역.
   우선순위 낮춤(2026-07-28 사용자 결정). 착수 전 결정 필요: 캐시에 엔진·생성시각 컬럼 추가(코어 스키마 변경),
   병합 정책(IGNORE vs REPLACE), 사용자 편집본·억제 목록 보호. 비용은 3,000곡 기준 $55~90 추정.

## 완료된 백로그
- **자동 업데이트 실설치 검증** — 0.14.0 설치본 → 0.15.0 델타 업데이트를 사용자 실확인(2026-07-28)
- **v0.9.1 릴리스 배포** — 델타(0.9.0→0.9.1, 6파일) + full + Setup, GitHub Releases Latest. "대상 언어 번역만 표시"(기본 켬) + 중국어 예외.
- **v0.9.0 릴리스 배포** — 델타(0.8.0→0.9.0) + full + Setup, GitHub Releases Latest(prerelease 아님). UI 다국어 19개어 + DeepL 키 DPAPI 암호화 + 설정 UI 개편 자동 업데이트 반영. ※ 업데이트 시 기존 평문 DeepL 키가 자동 암호화됨(구 0.8.0에선 키 미인식).
- **v0.8.0 릴리스 배포** — 델타(0.7.2→0.8.0, 6파일 패치) + full + Setup, GitHub Releases Latest(prerelease 아님). 오버레이 UX 옵션 5종 자동 업데이트 반영
- **v0.7.2 릴리스 배포** — 델타(0.6.2→0.7.2, 6파일 패치) + full, GitHub Releases Latest. 0.7.x 개선 전체 자동 업데이트 반영
- v0.6.0 첫 릴리스 배포 (GitHub Releases, Setup.exe + Velopack 자산)
- 트랙 메타 정제 검색 확장 (v0.6.1)
- QQ 실응답 수정 + 실검색 통합 검증 — 4개 제공자 전부 실API 확인 (v0.6.2)
- v0.6.2 릴리스 배포 — GitHub Releases Latest, 업데이트 피드에 0.6.2 등록 확인(0.6.0→0.6.2 경로 검증)

## 기술 결정 기록
- 스택: WPF 단일 (WinUI3/DirectWrite 불필요 판정 — M0 검증)
- SMTC 위치는 LastUpdatedTime 보간 필수
- 표시 체인: `tr:{target}`(DeepL) → `tr`(제공자). 키 없으면 제공자 번역만
- 캐시: `%LOCALAPPDATA%\LyricsX\translations.db` (translation_cache + lyrics_cache 테이블)
- 로그: `%LOCALAPPDATA%\LyricsX\app.log` / 설정: `settings.json`
- 함정 기록: WPF 개체 이니셜라이저는 생성자 후 실행 → 생성자에서 파이프라인 시작 금지(`Start()` 패턴)
- .NET 8 SDK 8.0.422, 새 셸: `$env:Path += ';C:\Program Files\dotnet'`

## 참조
- PRD: `C:\Users\AN020\.claude\plans\precious-cooking-raven.md`
- 원본(macOS): `C:\Users\AN020\LyricsX` / 포팅 참조: `external/LyricsKit`
