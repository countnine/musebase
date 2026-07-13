# PROGRESS — LyricsX for Windows

> 세션이 끊겨도 이 파일만 읽으면 재개 가능하도록 유지한다.
> 재개 방법: 세션 리셋 후 "이어서"라고 입력.

## ▶ 다음 세션 첫 작업
- [ ] .NET 8 SDK 설치 확인 (`dotnet --version`) → 솔루션/프로젝트 생성 (`LyricsX.sln`, spikes 3종)

## 마일스톤 현황
- [ ] **M0** 스파이크 (SMTC / 투명 클릭스루 오버레이 / DirectWrite 2단 렌더) ← 진행 중
- [ ] M1 Core 엔진 (LRC 파서 + NetEase/QQ/Kugou + 랭킹)
- [ ] M2 NowPlaying + SyncScheduler + 트레이
- [ ] M3 오버레이 완성
- [ ] M4 번역 계층(DeepL 폴백, 기본 KO) + 설정 패널
- [ ] M5 P1 (수동 검색, 캐시, 자동 실행, 업데이트, 패키징)

## 완료 항목
- [x] PRD 확정 (`C:\Users\AN020\.claude\plans\precious-cooking-raven.md`)
- [x] 저장소 스캐폴드 (git init, .gitignore, PROGRESS.md)
- [x] .NET 8 SDK 설치 시작 (winget, 백그라운드)

## 미해결 이슈
- (없음)

## 참조
- PRD: `C:\Users\AN020\.claude\plans\precious-cooking-raven.md`
- 원본(macOS): `C:\Users\AN020\LyricsX` — 동기화 로직 `LyricsX/Component/AppController.swift`, 오버레이 `LyricsX/Controller/KaraokeLyricsController.swift`
- 엔진 포팅 참조: https://github.com/MxIris-LyricsX-Project/LyricsKit (clone 예정)
- 운영 규칙: 5시간 세션 한도 60% 도달 추정 시 현 작업 단위 마무리 후 중단, 커밋+본 파일 갱신
