# PROGRESS — Musebase for Windows (구 LyricsX for Windows)

> **상태: windows-v0.10.0 (2026-07-17)** — 첫 Musebase 정식 릴리스(packId 클린 브레이크). 개명 + 거버넌스 + MPL-2.0 + 옵트인 텔레메트리 + Browser/Android 스파이크.
> 재개 방법: "이어서"라고 입력하면 아래 백로그부터 진행.

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
2. **자동 업데이트 실설치 검증** — 0.6.0 설치본에서 실제 다운로드·재시작까지 관찰(현재 피드 수준까지 검증됨)
3. 글자단위 외 UX 개선 여지 — 검색 결과 미리보기/수동 선택 UX, 오프셋 미세조정 등

## 완료된 백로그
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
