# Musebase.Android (Phase 2 — 가사 엔진 조립 + 앱 내 동기 가사 표시 + 다른 앱 위 오버레이)

`.NET for Android`(net8.0-android) 헤드. **아직 `Musebase.sln`에 포함하지 않는다** —
android 워크로드가 CI/모든 개발 머신에 없어도 메인 빌드가 깨지지 않게 하기 위함.
앱이 성숙하면 sln 등록 + `ci.yml`에 `dotnet workload install android` 단계를 추가한다.

골든룰: `Musebase.Core`/`Musebase.Engine`/`contracts/`는 수정 금지(.claude/agents/android.md).

## 구성

| 파일 | 역할 |
|---|---|
| `Services/MediaListenerService.cs` | `NotificationListenerService` — 알림 접근 권한의 앵커. 알림을 파싱하지 않고, `MediaSessionManager.GetActiveSessions(component)` 호출 자격만 제공. **앱 완전 종료용 `Unbind()`/`Rebind()`**(API 24+) — 알림 접근이 켜져 있으면 시스템이 이 서비스를 계속 바인드해 프로세스가 살아 있으므로, 종료 시 스스로 언바인드하고 앱을 다시 열 때 재바인드한다(권한 설정은 유지) |
| `Services/AndroidNowPlayingSource.cs` | `INowPlayingSource`(Musebase.Engine 계약)의 Android 구현 — 세션 선택(재생 중 우선), 콜백+500ms 폴링, 위치 보간(+1초 미만 역행 흡수). **재생 소스 선택**: `SetSource(mode, includeVideoApps, preferredSources)`로 자동/특정 앱 패키지 고정, 자동 모드에서는 영상·브라우저 앱(YouTube·크롬 등) 기본 제외(YouTube Music은 음악이라 포함). **선호 음악 앱**(`preferredSources`)을 하나라도 고르면 자동 모드에서 **그 앱들만** 후보가 된다(팟캐스트·영상 앱 차단). 비우면 종전 규칙. 감지된 세션 목록은 `ActiveSessionPackages`로 노출(설정 화면용) |
| `Services/AndroidEngineDispatcher.cs` | `IEngineDispatcher`의 Android 구현 — 메인 Looper `Handler` 기반 Post/주기 타이머(WpfEngineDispatcher와 대칭) |
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
  테두리 색이 번역 상태를 알린다(회색=API 꺼짐, 주황=실패·한도 초과, 흰색=정상). 접힌 상태에서도
  **새 가사 줄이 나오면 약 3초 펼쳤다 접는 peek**을 기본으로 하며 설정에서 끌 수 있다.

**세로 모드에서는 가사 밴드를 항상 가로 가운데로 정렬한다** — 세로 화면에서는 밴드가 폭을 거의 다 쓰므로
좌우로 옮길 여지가 없고, 어긋나면 글자가 한쪽으로 몰려 보인다. 그래서 세로에서는 드래그로 **높이만** 바꾸고
X는 중앙 고정(`Gravity=Top|CenterHorizontal`, `X=0`)이며, 가로 모드에서는 좌우도 자유롭게 배치한다.
회전 시에는 `KaraokeTextView.SetMaxWidth`로 새 화면 폭을 다시 알려 줘야 줄바꿈·가운데 정렬이 맞는다
(생성 시 폭을 고정해 두면 회전 후 어긋난다).

위치는 픽셀이 아니라 **화면 여유 공간 대비 비율**(`OverlayRatio`/`BubbleRatio`)로 저장하고
`OnConfigurationChanged`에서 다시 계산하므로, 화면을 돌리거나 해상도가 바뀌어도 화면 밖으로 나가지 않는다
(세로 밴드의 X 비율은 "중앙"을 뜻하는 0.5로 저장한다).
밴드는 창 크기가 확정된 뒤에야 좌표를 계산할 수 있어, 저장된 위치는 **처음 보이는 시점에 1회** 적용한다.

폰트 크기·색 커스터마이즈는 다음 단계.

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

> `adb install -r bin/Debug/net8.0-android/com.countnine.musebase-Signed.apk`로 재설치.
> 오버레이 권한은 설치형이 아니라 사용자가 직접 켜는 특수 권한이라, 앱 재설치 후에도
> 다시 켜야 할 수 있다.
