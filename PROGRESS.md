# PROGRESS — LyricsX for Windows

> 세션이 끊겨도 이 파일만 읽으면 재개 가능하도록 유지한다.
> 재개 방법: 세션 리셋 후 "이어서"라고 입력.

## ▶ 다음 세션 첫 작업
- [ ] **M1 시작**: `src/LyricsX.Core` 클래스 라이브러리 생성 → LRC 파서 포팅
  - 참조: `external/LyricsKit/Sources/LyricsCore/` (파서·모델), `external/LyricsKit/Sources/LyricsService/` (제공자)
  - 순서: ① Lyrics 모델 + LRC 파서(+단위 테스트) ② NetEase 제공자 ③ QQ ④ Kugou ⑤ 랭킹(`quality` 로직 포팅)

## 마일스톤 현황
- [x] **M0** 스파이크 — **완료** (2026-07-13)
  - [x] S1 SMTC: 세션 감지·트랙 변경 이벤트·타임라인(위치+LastUpdatedTime 보간) 검증 (`spikes/Spike.Smtc`)
  - [x] S2 오버레이: 투명·항상 위·클릭스루·전역 핫키(Ctrl+Alt+D 드래그 토글) 검증 (`spikes/Spike.Overlay`)
  - [x] S3 텍스트 렌더: WPF FormattedText 지오메트리(외곽선+그림자+카라오케 클립 채움) 품질 충족 → **DirectWrite/Vortice 불필요, 오버레이 스택은 WPF로 확정**
- [ ] M1 Core 엔진 (LRC 파서 + NetEase/QQ/Kugou + 랭킹) ← 다음
- [ ] M2 NowPlaying + SyncScheduler + 트레이
- [ ] M3 오버레이 완성 (Spike.Overlay 승격·확장)
- [ ] M4 번역 계층(DeepL 폴백, target_lang 설정, 기본 KO) + 설정 패널
- [ ] M5 P1 (수동 검색, 캐시, 자동 실행, 업데이트, 패키징)

## 기술 결정 기록
- **오버레이/렌더 스택 = WPF** (WinUI3+Win32+DirectWrite 대신): 투명·클릭스루·지오메트리 렌더 모두 스파이크로 검증됨. 카라오케 채움은 `OutlinedTextElement.KaraokeProgress` DP + 클립 방식.
- SMTC 타임라인은 앱별 갱신 주기가 달라 `LastUpdatedTime` 기반 보간 필수 (Spike.Smtc에 구현 예시).
- .NET 8 SDK 8.0.422 (winget 설치). `dotnet`은 새 셸마다 PATH 갱신 필요할 수 있음: `$env:Path += ';C:\Program Files\dotnet'`

## 완료 항목
- [x] PRD 확정 (`C:\Users\AN020\.claude\plans\precious-cooking-raven.md`)
- [x] 저장소 스캐폴드 + LyricsX.sln
- [x] M0 스파이크 전체 (위 참조)

## 미해결 이슈
- 클릭스루는 스타일 플래그로 적용 확인했으나 실제 마우스 통과는 육안 미검증 → M3에서 사용자 확인
- 다중 모니터 배치·전체화면 감지는 M3 범위

## 참조
- PRD: `C:\Users\AN020\.claude\plans\precious-cooking-raven.md`
- 원본(macOS): `C:\Users\AN020\LyricsX` — 동기화 로직 `LyricsX/Component/AppController.swift`, 오버레이 `LyricsX/Controller/KaraokeLyricsController.swift`
- 엔진 포팅 참조: `external/LyricsKit` (clone 완료, v main)
- 운영 규칙: 5시간 세션 한도 60% 도달 추정 시 현 작업 단위 마무리 후 중단, 커밋+본 파일 갱신
