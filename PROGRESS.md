# PROGRESS — LyricsX for Windows

> **상태: v0.6.2 (2026-07-14)** — QQ 실응답 수정 + 실검색 통합 검증 완료
> 재개 방법: "이어서"라고 입력하면 아래 백로그부터 진행.

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
1. **차기 릴리스(v0.6.2) 배포** — 누적 변경(글자카라오케·제공자·메타정제·QQ수정)을 `RELEASING.md` 절차로 배포하고, 이전 설치본(0.6.0)→신버전으로 자동 업데이트 실검증
2. **오버레이 실표시 검증** — 실제 재생 중 Kugou/QQ/NetEase 글자 카라오케가 오버레이에서 채워지는지 화면 확인(제공자 데이터는 실검증됨, 렌더 관찰만 남음)
3. 글자단위 외 UX 개선 여지 — 검색 결과 미리보기/수동 선택 UX, 오프셋 미세조정 등

## 완료된 백로그
- v0.6.0 첫 릴리스 배포 (GitHub Releases, Setup.exe + Velopack 자산)
- 트랙 메타 정제 검색 확장 (v0.6.1)
- QQ 실응답 수정 + 실검색 통합 검증 — 4개 제공자 전부 실API 확인 (v0.6.2)

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
