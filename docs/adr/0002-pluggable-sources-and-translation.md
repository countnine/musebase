# ADR 0002 — 플러그인형 가사 소스 · 번역 엔진 레지스트리

- 상태: 승인됨 (Accepted)
- 날짜: 2026-07-16
- 관련: [[0001-core-language]]
- 브랜치: `refactor/engine-extraction`

## 맥락

가사 소스는 LRCLIB(공식·공개 API)와 NetEase/Kugou/QQ(역공학 비공식 API + 암호화
가사 포맷) 4종이다. 공개 배포 시 비공식 3종은 각 서비스 ToS·저작권 리스크가 있다.
번역은 DeepL(API 키 필요) 단일 엔진뿐이라, 설치 후 키 없이 바로 쓸 무료 옵션과
고품질 유료 옵션을 선택·교체할 여지가 없다.

핵심 추상화는 이미 존재한다: `ILyricsProvider`(+`LyricsProviderBase<T>`),
`ITranslator`. 즉 새 소스/엔진 추가는 클래스 하나면 된다. 부족한 것은 **어떤 것을
켤지 관리하는 조합 계층**(레지스트리 + 설정 + 메타데이터)이다.

## 결정

`LyricsX.Core`에 **두 개의 레지스트리**를 추가하고, 조합은 설정으로 구동한다.

### 가사 소스
- `LyricsSourceDescriptor(Id, DisplayName, IsOfficialApi, Factory)` + `LyricsSourceRegistry`.
- `LyricsSourceRegistry.Build(enabledIds)`로 활성 소스만 인스턴스화 → `LyricsSearchService`에 주입.
- `IsOfficialApi` 메타로 **배포 프로파일**을 나눈다:
  - 공개 OSS 빌드 권장 기본 = `OfficialIds`(LRCLIB만).
  - 개인/사이드로드 = `AllIds`(전부). 설정 `EnabledLyricsSources`로 사용자 토글.
- 비공식 소스는 **삭제하지 않는다.** 기본 조합에서 빼고 opt-in으로 두어 코드 자산은
  지키되 공개 리스크만 낮춘다. 필요 시 `#if` 배포 프로파일로 컴파일 제외도 가능.

### 번역 엔진
- `TranslatorDescriptor(Id, DisplayName, RequiresApiKey, IsFree, Factory)` + `TranslatorRegistry`.
- 설정 `TranslationEngine`으로 선택, `TranslatorOptions`로 키/엔드포인트 주입.
- 기본 해석(`EffectiveTranslationEngine`): DeepL 키가 있으면 `deepl`(기존 사용자 보존),
  없으면 `libretranslate`(무키 무료로 설치 후 바로 동작).
- 등록 엔진:
  | Id | 키 | 무료 | 품질 | 비고 |
  |---|---|---|---|---|
  | `libretranslate` | ✕(공개 인스턴스) | ○ | 중 | 오픈소스·합법. 자체호스팅/엔드포인트 교체 가능 |
  | `deepl` | ○ | 무료/유료 티어 | 상 | 기존 |
- 확장 여지: MyMemory(무키, 소스어 필요), **LLM(Claude/GPT 등, 고품질 유료)**,
  Azure/Google Cloud 등은 `ITranslator` 구현 + 레지스트리 한 줄로 편입.
- **폴백 체인**은 `ITranslator`를 구현하는 `CompositeTranslator`(데코레이터)로
  인터페이스 변경 없이 추가 가능(무료 우선 → 실패 시 유료 등).
- **비공식 회피**: Google 웹 비공식 번역은 가사 비공식 API와 같은 리스크라 등록하지 않는다.

## 귀결

- 소스/엔진 구현·인터페이스·레지스트리는 `LyricsX.Core`(이식성 유지) →
  Android/서버도 같은 레지스트리로 켜고 끈다.
- 선택/키/토글은 설정(App). 조합 로직은 레지스트리 정적 메서드.
- 라이선스·품질·키 필요 여부가 **메타데이터로 표면화**되어 UI(설정창 체크박스/콤보)와
  배포 프로파일이 이를 그대로 읽어 쓴다.
- 주의: LibreTranslate 공개 인스턴스(`libretranslate.com` 등)는 유료화/레이트리밋 변동이
  있으므로, 견고한 운영은 자체호스팅 또는 엔드포인트 교체를 전제로 한다(설정으로 노출).

## 후속(이 ADR 범위 밖)

- SettingsWindow에 소스 체크박스 + 번역 엔진 콤보 + 엔드포인트/키 입력 UI.
- `CompositeTranslator` 폴백 체인, LLM 번역기.
- 가사 소스 변경의 런타임 반영(현재는 재시작 시 반영; 번역 엔진은 저장 시 즉시 반영).
