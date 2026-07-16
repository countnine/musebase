# Musebase (구 LyricsX) — 에이전트 공통 지침

데스크톱 가사 오버레이의 멀티플랫폼 모노레포. 제품/브랜드는 소문자 `musebase`,
코드(네임스페이스/어셈블리)는 `Musebase.*`, UI 표시명은 "Musebase".

## 소유권 지도 + 골든룰

| 경로 | 소유 | 내용 |
|---|---|---|
| `src/Musebase.Core/` | **core 에이전트 전용** | 도메인(가사 파싱·검색·번역·캐시), UI 무관 net8.0 |
| `src/Musebase.Engine/` | **core 에이전트 전용** | 오케스트레이션(`INowPlayingSource`, `LyricsCoordinator`, `PlaybackViewState`, `LyricsEngineFactory`) |
| `contracts/` | **core 에이전트 전용** | 직렬화 계약 문서(플랫폼 간 정렬 기준) |
| `src/Musebase.Windows/` | windows 에이전트 | WPF 앱(SMTC, 오버레이, 트레이, 설정, i18n) |
| `src/Musebase.Android/` | android 에이전트 | (예정) .NET for Android |
| `src/Musebase.Browser/` | browser 에이전트 | (예정) ASP.NET WS 방송 + 웹 디스플레이 |
| `apple/` | apple 에이전트 | (예정) Swift — 코드 비공유, `contracts/`로만 정렬 |
| `tests/`, `docs/`, `scripts/`, `tools/` | 공용 | 소유 경로에 대응하는 부분만 수정 |

**골든룰: 코어(`Musebase.Core`/`Musebase.Engine`/`contracts/`)는 core 에이전트만 수정한다.**
플랫폼 에이전트는 이를 참조(사용)만 하고, 변경이 필요하면 "코어 변경 요청"으로 분리해 보고한다.

## 빌드·테스트 (Windows 개발 머신)

```powershell
$env:Path += ';C:\Program Files\dotnet'   # dotnet이 PATH에 없음 — 세션마다 필요
dotnet build Musebase.sln -c Release       # 전체 빌드 (경고 0 유지)
dotnet test tests/Musebase.Core.Tests      # 유닛 테스트 (전부 통과 유지)
```

- 빌드 전 실행 중 `Musebase`(구 `LyricsX`) 프로세스를 종료할 것 — DLL 잠금으로 실패한다.
- **테스트용 단일 exe는 반드시 `publish-test\`에 생성**(바탕화면 등 임의 위치 금지):
  `dotnet publish src/Musebase.Windows/Musebase.Windows.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-test`
- 커밋 메시지 heredoc은 Bash 도구에서 `<<'EOF'`로 (PowerShell `@'...'@`는 `@` 혼입 사고 이력).

## 브랜치·릴리스

- `master` = 안정(보호됨, CI 통과 필수) / `beta` = 통합·프리릴리스 검증 / 작업 브랜치 `<type>/<platform>/<slug>` (예: `feat/windows/overlay-x`, 코어는 `core/<slug>`) — PR로만 머지.
- 릴리스는 플랫폼별 독립 SemVer + 접두 태그(`windows-vX.Y.Z`, `android-vX.Y.Z`, …). Windows 릴리스 절차는 `RELEASING.md`(Velopack, packId `Musebase`).
- 베타 배포: `beta` 브랜치 + `gh release create <tag> <exe> --target beta --prerelease` — 자동 업데이트·홈페이지는 프리릴리스를 무시하므로 정식 사용자에 영향 없음.

## 주의(호환성 불변식)

- 설정 직렬화 키·식별자는 영어 유지(예: `KaraokeColor`) — UI 문구만 현지화.
- DPAPI entropy 문자열 `"LyricsX.DeepL.v1"`(Secret.cs)은 **절대 변경 금지**(기존 암호문 복호 불가).
- `PlaybackViewState` 필드 변경은 `contracts/playback-view-state.md`와 테스트를 함께 갱신(코어 에이전트).
- 공개 배포 빌드의 가사 소스는 법적 리스크 시 `LyricsSourceRegistry.OfficialIds`(LRCLIB만)로 좁힐 수 있다(ADR-0002).

## 라이선스

MPL-2.0. `src/Musebase.Core`의 상당수 파일은 ddddxxx의 LyricsKit(MPL-2.0) 포팅 — 해당 파일은
MPL 유지 의무가 있으므로 헤더의 "포팅" 출처 표기를 **지우지 말 것**. MPL 파일의 코드 조각을
다른 파일로 복사하지 말 것(그 파일도 MPL이 된다) — 필요하면 인터페이스 호출로 사용.
