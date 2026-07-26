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
| `MainActivity.cs` | 앱 내 가사 UI(대체 확인용 유지) + **오버레이 권한 안내/요청**(`Settings.CanDrawOverlays`→`ACTION_MANAGE_OVERLAY_PERMISSION`) + **오버레이 켜기/끄기 토글** + **"번역 설정" 버튼**(→ `SettingsActivity`). 검색 상태 + 현재 줄 + 번역, `StateChanged`/`StatusChanged` 구독 |
| `SettingsActivity.cs` | **번역 설정 화면**(코드 UI, Exported=false) — 엔진 스피너(MyMemory/DeepL/Google Cloud Translation/끄기) + **선택한 엔진의 API 키**(키를 쓰는 엔진에서만 표시, 문구·값이 엔진을 따라감, 눈 토글) + 대상 언어(비면 로케일 기본) + **API 번역 사용** 체크박스(기본 켬 — 끄면 새 번역 요청 없이 캐시된 번역만, 유료 사용량 차단) + **선택 엔진 실패 시 무료(MyMemory) 자동 전환** 체크박스(기본 꺼짐, 켜면 공개 서버 전송 안내 표시). 저장 시 `AndroidSettings`에 반영 → `MusebaseApp.ApplyTranslationSettings()`로 **재시작 없이** 엔진 재구성(새 엔진은 다음 곡부터) |
| `Services/AndroidSettings.cs` | `ISharedPreferences`(앱 private) 래퍼 — `TranslationEngine`/`DeeplApiKey`/`GoogleApiKey`/`TargetLanguage`/`TranslationFallbackToFree`/`ApiTranslationEnabled`(엔진 id로 읽고 쓰는 `Get/SetTranslationApiKey` 포함). **앱 private 저장이며 디스크 암호화는 아님**(Windows DPAPI와 다름 — 루팅/백업 추출 시 평문 노출 가능). 직렬화 키는 영어 식별자 유지 |
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

오버레이 위치/폰트 커스터마이즈는 다음 단계.

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
