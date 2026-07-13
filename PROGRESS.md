# PROGRESS — LyricsX for Windows

> **상태: v0.1.0 — PRD의 P0(MVP) + P1 주요 기능 완성 (2026-07-13)**
> 재개 방법: "이어서"라고 입력하면 아래 백로그부터 진행.

## 완성된 것 (전부 검증됨)
- **M0** 스파이크 3종 → 스택 확정 (WPF, SMTC 보간, 지오메트리 렌더)
- **M1** Core 엔진: LRC 파서 / LRCLIB·NetEase 제공자(EAPI 암호화) / 품질 랭킹·병렬 집계 — 32 유닛 테스트
- **M2** SMTC 재생 감지 + 스트리밍 검색(첫 결과 ~0.9s) + 트레이
- **M3** 오버레이: 이중언어 2단 + 카라오케 채움 + 클릭스루 + 이동 모드 — **사용자 실검증**
- **M4** DeepL 번역 폴백(tr:{target}→tr 체인, SQLite 라인 캐시) + 설정 창 — **사용자 실검증**
- **M5** 가사 캐시(<100ms 재표시·오프라인) / 자동 실행 토글 / .ico / 수동 검색 창 / 배포 패키징
- 배포: `artifacts\LyricsX-Windows-v0.1.0-win-x64.zip` (70MB, self-contained 단일 exe)

## 백로그 (다음 작업 후보, 우선순위 순)
1. **전체화면 감지** — 게임/영상 전체화면 시 오버레이 자동 숨김 (원본 CGWindowList 상당 → Win32 `SHQueryUserNotificationState` 또는 foreground 창 RECT 비교)
2. **QQ Music/Kugou 제공자** — 중국 곡 커버리지 확대 (KRC/QRC 복호화 포팅, `external/LyricsKit/Sources/LyricsService/Parser/` 참조)
3. **글자 단위 카라오케** — NetEase yrc/klyric 파싱 + InlineTimeTags 기반 채움 (현재는 라인 단위 진행률)
4. **자동 업데이트** — Velopack + GitHub Releases
5. **오버레이 스타일 설정** — 색상/외곽선/카라오케 색 커스터마이즈 UI
6. 검색 실패 시 재시도/트랙 메타 정제(feat. 표기 제거 등) 플러그인 (원본 LyricsSearchRequestPlugin 상당)

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
