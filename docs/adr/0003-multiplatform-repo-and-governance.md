# ADR-0003: 멀티플랫폼 리포 구조·거버넌스와 제품 개명(musebase)

- 상태: 승인 (2026-07-16)
- 결정자: countnine (1인 개발 + 다중 코딩 에이전트)
- 관련: ADR-0001(코어 언어 .NET 유지), ADR-0002(플러그형 소스·번역)

## 맥락

Windows WPF 앱에서 출발해 Core/Engine(UI 무관 net8.0) 분리를 마쳤고, Android·브라우저
디스플레이·macOS/iOS로 확장한다. 개발 주체는 1인 + 플랫폼별 코딩 에이전트이며 멀티플랫폼
운영 경험이 없다. 목표: 안전하게, 적은 공수로, 병행 개선. 장기적으로 제품/서비스명을
통일한다.

## 결정

1. **제품 개명: LyricsX → musebase.** 브랜드/리포/도메인은 소문자 `musebase`,
   코드(네임스페이스/어셈블리)는 `Musebase.*`, UI 표시명 "Musebase", Velopack packId `Musebase`.
   플랫폼이 늘기 전(Phase 0)에 일괄 수행 — 나중이면 N개 플랫폼을 전부 고쳐야 한다.
   - packId 변경 = 구 설치본(≤0.9.x) 자동 업데이트 단절. 현 사용자 규모상 **클린 브레이크 수용**
     (릴리스 노트 명시, `%LOCALAPPDATA%` 데이터는 첫 실행 시 자동 이전).
   - DPAPI entropy `"LyricsX.DeepL.v1"`은 기존 암호문 복호를 위해 **유지**.
2. **모노레포.** .NET 생태계 공유(솔루션·CI·원자적 코어 변경)가 최대 이점. 홈페이지만 별도 리포.
3. **투코어 전략.** .NET 코어 하나로 Windows(WPF) + Android(**.NET for Android** — Kotlin/Flutter
   아님) + 브라우저(.NET 서버+JS). **Apple(macOS/iOS)은 Swift**(원조 LyricsX/LyricsKit 자산 진화).
   세 번째 코어는 만들지 않는다. 두 코어는 코드가 아니라 **직렬화 계약(`contracts/`) + ADR**로 정렬.
4. **최소 거버넌스.**
   - 에이전트별 git worktree로 동시 작업(산출물은 gitignore → 충돌 없음).
   - **골든룰: 코어(`Musebase.Core`/`Musebase.Engine`/`contracts/`)는 core 에이전트만 수정.**
     플랫폼 에이전트는 사용만. 에이전트 정의는 `.claude/agents/`, 소유권 지도는 루트 `CLAUDE.md`.
   - CI 게이트 1개(`.github/workflows/ci.yml`): PR/push마다 솔루션 빌드+테스트. 모노레포라
     코어 변경 시 전 .NET 헤드가 자동으로 함께 검증된다. CODEOWNERS로 코어 경로 경량 보호.
   - 트렁크형 브랜치: `master`(보호+CI 필수) / `beta`(프리릴리스 검증) / `<type>/<platform>/<slug>`.
     gitflow·develop 없음(솔로에 과함).
5. **플랫폼별 독립 SemVer + 접두 태그**(`windows-`/`android-`/`browser-`/`macos-`/`ios-`).
   릴리스 발행마다 `notify-homepage.yml`이 홈페이지를 갱신(프리릴리스 제외 안전망 유지).
   코어/엔진은 별도 릴리스 없이 head 빌드.

## 대안과 기각 사유

- **멀티리포**: 코어 변경이 N개 리포 버전 범프로 번져 솔로 공수 폭증 → 기각.
- **Android를 Kotlin/Flutter로**: 세 번째 코어(가사 파싱·검색·번역 로직 중복) 유지 비용이
  네이티브 이점을 압도 → 기각. .NET for Android로 `INowPlayingSource`만 플랫폼 구현.
- **Apple도 .NET(MAUI)로**: 원조 Swift 자산(LyricsKit)이 이미 성숙 + macOS 통합 품질 → Swift 유지.
- **개명을 나중에**: 플랫폼 수만큼 파급이 곱해짐 → Phase 0 일괄로 기각.

## 결과

- Phase 0(본 ADR): 개명 일괄 + LICENSE(MPL-2.0)·출처 표기 + ci.yml/CLAUDE.md/agents/CODEOWNERS/contracts.
- Phase 1: `src/Musebase.Browser`(WS 방송 + 웹 디스플레이)로 계약 검증 → Phase 2: Android → Phase 3: Apple.
- 후속 운영 작업: GitHub 리포 개명(`LyricsX-Windows`→`musebase`, 구 URL 리다이렉트),
  master 룰셋에 CI 통과 필수 추가, 홈페이지 리포 개명(선택)과 플랫폼별 버전 표시.
