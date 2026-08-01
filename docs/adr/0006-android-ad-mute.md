# ADR-0006: Android Spotify 광고 뮤트 — 별도 앱이 아니라 musebase 옵인 기능으로

- 상태: 승인 (2026-07-30)
- 결정자: countnine
- 관련: ADR-0003(모노레포·거버넌스), `src/Musebase.Android/README.md`

## 맥락

Windows용 별도 앱 [Mutefy](https://github.com/countnine/mutefy)를 만들어 Spotify 광고 구간에
Spotify만 음소거하고, 그동안 지정 폴더의 MP3를 랜덤 재생하는 기능을 완성했다.
같은 것을 안드로이드에서도 쓰고 싶다는 요구가 나왔고, musebase Android가 이미
`MediaSessionManager` 기반 재생 감지(`AndroidNowPlayingSource`)와 알림 접근 권한을 갖고 있다.

조사에서 두 가지가 드러났다.

**감지는 안드로이드가 더 낫다.** 표준 메타데이터 키 `android.media.metadata.ADVERTISEMENT`가
있고 Spotify가 이를 설정한다. 미디어 ID도 `spotify:ad`로 시작한다. Windows에서 고생한
현지화 문자열 추측(한국 광고 제목이 "광고 없이 음악을 감상하세요."였다)이 필요 없다.

**음소거는 안드로이드가 근본적으로 열등하다.** 다른 앱의 볼륨만 조절하는 공개 API가 없다.
기존 구현체(hushify)도 결국 `AudioManager`로 `STREAM_MUSIC` 전체를 0으로 내린다.

## 결정

1. **별도 앱이 아니라 musebase Android의 옵인 기능으로 넣는다.** 별도 앱이면 사용자가
   알림 접근을 두 번 허용하고, 알림바가 두 개 뜨고, 두 앱이 각각 500ms로 `MediaSessionManager`를
   폴링한다. 감지 계층(`AndroidNowPlayingSource`, `MediaListenerService`)이 이미 있으므로
   재구현할 이유도 없다.
2. **기본 꺼짐.** musebase는 가사 앱이고 이미 배포 중이다. 감지가 틀리면 가사 앱이 사용자의
   기기 미디어 볼륨을 0으로 만든다 — 옵인으로 폭발 반경을 제한한다.
3. **Mutefy 코드를 이식하지 않고 새로 쓴다.** musebase는 MPL-2.0, Mutefy는 GPL-3.0
   (MuteSpotifyAds·EZBlocker3에서 파생된 휴리스틱 때문). GPL 코드를 넣으면 musebase 전체가
   GPL이 된다. 안드로이드는 공식 플래그를 쓰므로 **차용할 휴리스틱이 애초에 없다** —
   법적 편법이 아니라 실제로 다른 구현이다. 상수값(디바운스 150ms, 상한 180초)만
   Windows 실측 결과를 근거로 가져온다.
4. **포그라운드 서비스를 새로 만들지 않는다.** 알림 접근이 켜져 있으면 시스템이
   `MediaListenerService`를 계속 바인드해 프로세스가 살아 있다. `MusebaseApp`이
   `AdMuteController`를 들고 있으면 되고, 알림바 항목이 늘지 않는다.
5. **코어/엔진을 건드리지 않는다(골든룰).** 광고 여부를 `Engine.TrackInfo`에 넣으면
   contracts 변경이 되므로, `AndroidNowPlayingSource.IsAdvertisement`라는
   Android 전용 속성으로 노출한다.
6. **대상은 Spotify 전용**(`com.spotify.music`). 다른 앱이 이 플래그를 어떻게 쓰는지
   검증된 바 없다.
7. **MP3 채우기는 넣지 않는다.** `STREAM_MUSIC`을 0으로 내리면 채우기 음악도 함께 죽는다.
   우회(알람 스트림 등)는 미디어 볼륨 슬라이더가 안 먹고 기기에 따라 블루투스 대신
   스피커로 나갈 수 있어, 실기기 스파이크로 라우팅·볼륨 거동을 확인한 뒤 따로 판단한다.

## 결과

- 감지 신호는 신뢰도 순으로 셋을 겹친다(`AdSignals` — 2026-08-01에 `Musebase.Engine`으로 옮겼다):
  ① 표준 광고 플래그 → ② `spotify:ad` 미디어 ID → ③ `artist=Spotify` + 빈 앨범(Windows 실측 폴백).
  ③의 앨범 조건이 안전장치다 — Spotify의 실제 곡은 앨범이 항상 채워져 있다.
- **.NET for Android 바인딩에 `MetadataKeyAdvertisement` 상수가 없어** 플랫폼 키 문자열
  `"android.media.metadata.ADVERTISEMENT"`를 직접 쓴다. `Microsoft.Android.Ref.34`의
  `Android.Media.MediaMetadata`에는 `MetadataKeyMediaId` 등 56개가 있지만 이것만 빠져 있다(직접 확인).
- **볼륨을 남기지 않는 것이 이 기능의 최우선 불변식이다.** Windows판에서 강제 종료로 실제로
  겪었고, 안드로이드는 앱별이 아니라 기기 전체 볼륨이라 피해가 더 크다. 원래 볼륨을
  `SharedPreferences`(`AdMuteSavedVolume`)에 **볼륨을 내리기 전에** 기록해, 프로세스가 광고 도중
  죽어도 다음 실행의 `RestoreOrphanedVolume()`이 되돌린다. 기능이 꺼져 있어도 앱 시작 시 호출한다.
- 사용자와 볼륨을 다투지 않는다 — 뮤트 중 볼륨이 0이 아니게 되면 그 광고 구간은 포기한다.
- 유닛 테스트는 붙지 않는다. `Musebase.Android`는 `Musebase.sln` 밖이고 `Musebase.Core.Tests`는
  net8.0이다. 그래서 판정·디바운스 로직(`AdSignals`/`AdDecision`)을 **Android 타입 무의존**으로
  써 두었다 — 나중에 코어 변경 요청으로 `Musebase.Engine`에 옮기면 그대로 테스트할 수 있다.

### 보완 (2026-08-01) — `AdSignals`를 Engine으로

위의 "나중에"가 왔다. **광고 구간에는 가사를 찾지 않는다**를 Windows에도 넣으면서 판정 규칙이
두 플랫폼 공통이 됐으므로 `AdSignals`를 `Musebase.Engine`으로 옮겼다(`AdSignalsTests` 5건 추가).

- 뮤트 상태 기계(`AdDecision`/`AdSignal`)는 **Android에 남는다** — 볼륨을 다루는 안전장치라
  가사 쪽에는 필요 없다.
- Windows에는 광고 플래그도 `mediaId`도 없어 신호 ③만 쓴다(`LooksLikeAd(artist, album)` 오버로드).
  디바운스도 없다 — ③은 아티스트가 비면 false라서 곡 전환 순간의 빈 메타데이터를 광고로 보지 않고,
  설령 한 틱 틀려도 가사 검색이 잠깐 늦어질 뿐 볼륨처럼 사용자에게 남는 피해가 없다.
- Windows에서 광고를 뮤트하고 MP3를 채우는 일은 **별도 앱(Mutefy)** 이 한다. Musebase Windows는
  광고를 **표시하지 않을** 뿐이며, 두 앱은 서로 간섭하지 않는다(Mutefy는 Spotify 프로세스의
  오디오 세션만 음소거하고, 필러는 `WasapiOut` 직접 출력이라 SMTC 세션을 만들지 않는다).

## 대안 (기각)

- **별도 앱(Mutefy Android)** — 제품 경계는 명확하지만 감지 계층 재구현 + 알림 접근 재허용 +
  알림바 2개 + 폴링 2중. 두 앱을 함께 쓰는 사용자에게 손해가 크다.
- **오디오 포커스 더킹**(`AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK`) — 광고가 완전히 안 꺼지고
  약 20% 볼륨으로 남는다. 문서상 15초 이내 용도인데 실측 광고는 55~58초다.
- **`AUDIOFOCUS_GAIN_TRANSIENT`** — Spotify가 일시정지해 버려 **광고 시간이 흐르지 않는다**.
  포커스를 놓으면 광고가 그 지점부터 다시 재생돼 영원히 끝나지 않는다.
