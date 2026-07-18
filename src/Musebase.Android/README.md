# Musebase.Android (Phase 2 — 가사 엔진 조립 + 앱 내 동기 가사 표시 + 다른 앱 위 오버레이)

`.NET for Android`(net8.0-android) 헤드. **아직 `Musebase.sln`에 포함하지 않는다** —
android 워크로드가 CI/모든 개발 머신에 없어도 메인 빌드가 깨지지 않게 하기 위함.
앱이 성숙하면 sln 등록 + `ci.yml`에 `dotnet workload install android` 단계를 추가한다.

골든룰: `Musebase.Core`/`Musebase.Engine`/`contracts/`는 수정 금지(.claude/agents/android.md).

## 구성

| 파일 | 역할 |
|---|---|
| `Services/MediaListenerService.cs` | `NotificationListenerService` — 알림 접근 권한의 앵커. 알림을 파싱하지 않고, `MediaSessionManager.GetActiveSessions(component)` 호출 자격만 제공 |
| `Services/AndroidNowPlayingSource.cs` | `INowPlayingSource`(Musebase.Engine 계약)의 Android 구현 — 세션 선택(재생 중 우선), 콜백+500ms 폴링, 위치 보간(+1초 미만 역행 흡수) |
| `Services/AndroidEngineDispatcher.cs` | `IEngineDispatcher`의 Android 구현 — 메인 Looper `Handler` 기반 Post/주기 타이머(WpfEngineDispatcher와 대칭) |
| `MusebaseApp.cs` | 커스텀 `Application` — `LyricsEngineFactory.Create`로 엔진 1회 조립(화면 회전에도 유지). 소스=레지스트리 전체(개인용), 번역=MyMemory(무키·무료 기본), 대상 언어=기기 로케일, 캐시=`FilesDir/translations.db`, 텔레메트리=Noop |
| `Services/OverlayService.cs` | 포그라운드 서비스 — `WindowManager`의 `TYPE_APPLICATION_OVERLAY` 뷰(하단 중앙, 반투명 둥근 카드)로 다른 앱 위에 가사 표시. 코디네이터 `CurrentLineChanged`/`LineProgressChanged` + 소스 `IsPlayingChanged`만 **구독**(엔진 재조립 안 함). 재생 중+라인 있을 때만 표시, 터치 완전 통과. Android 8+ 알림 채널 + "정지" 액션 |
| `Views/KaraokeTextView.cs` | 커스텀 뷰 — 베이스(흰색) 위에 채움색(노랑 `#FFEB3B`)을 진행 글자까지 클립해 덧그리는 **글자 단위 카라오케**. 태그(`InlineTimeTags.CharIndexAt`) 있으면 글자 위치, 없으면 라인 비율 폴백. 100ms 갱신 사이를 앵커+실시간으로 60fps 보간(`postInvalidateOnAnimation`). `StaticLayout`으로 멀티라인/가운데정렬 대응 |
| `MainActivity.cs` | 앱 내 가사 UI(대체 확인용 유지) + **오버레이 권한 안내/요청**(`Settings.CanDrawOverlays`→`ACTION_MANAGE_OVERLAY_PERMISSION`) + **오버레이 켜기/끄기 토글**. 검색 상태 + 현재 줄 + 번역, `StateChanged`/`StatusChanged` 구독 |
| `AndroidManifest.xml` | INTERNET + `SYSTEM_ALERT_WINDOW` + `FOREGROUND_SERVICE`(+`_SPECIAL_USE`, Android 14) + `POST_NOTIFICATIONS`. `<service>`(리스너/오버레이)/`<activity>`/`<application android:name>`은 C# 특성에서 생성·병합 |

SQLite: `Microsoft.Data.Sqlite`(Musebase.Core 참조)가 `SQLitePCLRaw.bundle_e_sqlite3`를 통해
net8.0-android 네이티브 `libe_sqlite3.so`를 자동 포함하므로 별도 PackageReference/초기화가 필요 없다.

오버레이 서비스는 `specialUse` 포그라운드 타입을 쓴다. Google Play 심사는 매니페스트에
`PROPERTY_SPECIAL_USE_FGS_SUBTYPE` 프로퍼티를 요구하지만, 사이드로드/실기기 런타임에는
강제되지 않으므로 이 스파이크에는 넣지 않았다(스토어 배포 시 추가 필요).

DeepL 키 입력 UI·오버레이 위치/폰트 커스터마이즈는 다음 단계.

## 빌드 환경 (2026-07 이 머신에서 확인한 상태)

- .NET SDK 8.0.423 (`C:\Program Files\dotnet`, PATH에 없음 — 세션마다 `$env:Path += ';C:\Program Files\dotnet'`)
- **android 워크로드: 설치됨** (`dotnet workload install android` — Microsoft.Android.Sdk.Windows 34.0.154). 관리자 승격 없이 성공했음(MSI가 UAC 자동 승인 환경).
- **JDK: 없음** (`where.exe java` 실패, `JAVA_HOME` 미설정) → **JDK 17+ 필요** (권장: Microsoft OpenJDK 17)
- **Android SDK: 없음** (`%LOCALAPPDATA%\Android\Sdk` 부재, `ANDROID_HOME` 미설정) → **platform-tools + platforms;android-34 + build-tools 필요**

이 상태에서 `dotnet build src/Musebase.Android -c Debug`는 다음 오류로 중단된다:

```
error XA5300: Android SDK 디렉터리를 찾을 수 없습니다. (https://aka.ms/dotnet-android-install-sdk)
error XA5300: 'AndroidSdkDirectory' MSBuild 속성을 사용자 지정 경로로 설정합니다.
```

단, C# 소스 자체는 Mono.Android(API 34) 참조 어셈블리에 대해 **경고 0으로 컴파일 검증 완료**
(csc 직접 호출) — 남은 것은 JDK/SDK만 있으면 되는 패키징 단계다.

## 사용자가 할 일 — APK 빌드까지

가장 쉬운 길은 .NET for Android의 자동 설치 타깃이다(관리자 불필요, 사용자 폴더에 설치):

```powershell
$env:Path += ';C:\Program Files\dotnet'
# 1) JDK + Android SDK를 지정 폴더에 자동 다운로드/설치
dotnet build src/Musebase.Android -t:InstallAndroidDependencies -f net8.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:LOCALAPPDATA\Android\jdk" `
  -p:AcceptAndroidSDKLicenses=true

# 2) 빌드 (이후에는 같은 -p: 경로 지정 또는 ANDROID_HOME/JAVA_HOME 환경변수 설정)
dotnet build src/Musebase.Android -c Debug `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:LOCALAPPDATA\Android\jdk"
```

수동 설치 대안: Microsoft OpenJDK 17(msi) + Android Studio(또는 commandline-tools)로
`%LOCALAPPDATA%\Android\Sdk`에 platform-tools/android-34 설치 후 `JAVA_HOME`/`ANDROID_HOME` 설정.

APK 산출 경로(디버그 서명 포함): `src/Musebase.Android/bin/Debug/net8.0-android/com.countnine.musebase-Signed.apk`

## 폰에서 테스트 (사이드로드)

1. 폰 USB 디버깅 켜고 `adb install <위 APK 경로>` — 또는 APK를 폰에 복사해 설치
   (출처를 알 수 없는 앱 허용 필요).
2. Musebase 앱 실행 → "알림 접근 권한 설정 열기" 버튼 → 설정 목록에서 **Musebase** 토글 ON.
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
