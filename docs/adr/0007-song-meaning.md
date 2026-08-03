# ADR-0007: 곡의 의미 — 외부 자료 수집 + 요약

- 상태: 채택 (2026-08-01)
- 관련: ADR-0005(개인 가사 서버), `contracts/lyrics-api.md`

## 배경

가사는 있는데 **그 곡이 무슨 이야기인지**는 어디에도 없다. 관리자 화면에서 곡을 열었을 때
가사 위에 "이 곡의 의미"를 한국어로 보여 주고, 나중에 Windows·Android 앱에서도 확인할 수
있게 하려 한다.

## 결정

### 1. Musixmatch의 "Meaning"은 링크가 기본, 자료로 쓰려면 켜야 한다

공개 API(`track.search` / `matcher.track.get` / `track.lyrics.get` / `track.snippet.get` /
`artist.search` …)에 **meaning 엔드포인트가 없다.** 그래서 처음에는 링크만 걸었다.

**2026-08-02 변경**: 곡 페이지에서 그 텍스트를 꺼낼 수 있음을 확인해 **선택적 자료원**으로 넣되,
**기본은 꺼 둔다**(`MUSEBASE_MEANING_SOURCES`에 `musixmatch`를 명시해야 쓰인다). 그렇게 한 이유:

- **그 텍스트는 사람이 쓴 해설이 아니다.** 페이지 HTML의 `__NEXT_DATA__` 안 `lens` 블록에 있고,
  같은 블록에 `moods`·`themes`·콘텐츠 등급이 함께 들어 있다 — 가사를 기계로 분석한 묶음이다.
  이걸 자료로 넣으면 **LLM이 쓴 글을 다시 LLM에 넣어 요약**하는 셈이라, "근거에 묶어 둔다"는
  이 기능의 전제가 약해지고 무엇에 근거했는지 추적할 수 없다.
- 그래서 소스 이름을 `Musixmatch (AI 분석)`으로 두어 출처 표기에 성격이 그대로 드러나게 하고,
  프롬프트에도 "AI 분석 자료는 사실 근거가 약하니 다른 자료와 어긋나면 다른 자료를 따른다"를 넣었다.
- 약관상 스크래핑 금지라는 점은 그대로다. **켜는 판단과 그 위험은 운영자의 몫**이고, 기본값이
  꺼져 있으므로 배포본이 저절로 그 상태가 되지는 않는다.

**곡 페이지 주소는 공식 API(`track.search`의 `track_share_url`)로만 얻는다.** 규칙으로 만들면
안 된다 — 실측에서 `/lyrics/Pearl-Jam/Even-Flow`가 오류 없이 200을 주면서 조용히
`/lyrics/Pearl-Jam/Alive`(다른 곡)로 넘어갔다. 검색 결과 페이지를 서버가 긁는 길도 익명 요청이
로그인 페이지로 리다이렉트되어 막혀 있다.

### 2. 소스는 셋을 겹친다 — Genius · Last.fm · Wikipedia

| 소스 | 인증 | 얻는 것 |
|---|---|---|
| Genius | 무료 Client Access Token(OAuth 플로우 불필요) | `/songs/{id}?text_format=plain`의 `description`(About) |
| Last.fm | 무료 API 키 | `track.getInfo`의 `wiki.content` |
| Wikipedia | **없음** | 곡 문서 도입부(`prop=extracts`) |

하나가 비어도 나머지가 채운다. 셋을 **병렬로** 부르고 실패는 무시한다 —
`HttpRemoteLyricsCache`의 조용한 강등과 같은 원칙이다.

#### Songfacts는 넣지 않는다 (2026-08-03 재검토)

> 처음 이 문서에는 "Songfacts는 API가 없어 제외했다"고 적었다. **그 서술은 사실이 아니었다** —
> 확인해 보니 [공식 API](https://www.songfacts.com/blog/pages/songfacts-api)가 있고
> songfacts·artistfacts·차트 순위를 제공한다(상업용, 가격은 문의). 틀린 근거로 남겨 두면 같은
> 검토를 되풀이하게 되므로 실제 조사 결과로 바꿔 적는다.

**내용은 우리가 본 것 중 가장 좋다.** 사람이 쓴 사실 기반 서술이라 우리 파이프라인이 원하는
"근거"에 정확히 들어맞는다. 실제 `Even Flow` 페이지에는
*"wrote the lyrics about a homeless person who is neglected by society"* 가 있다 —
위키피디아만으로는 `자료 부족`이 났던 바로 그 곡이다.

**그래도 긁지 않는다. 약관이 이 행위를 지목해 금지한다.**

> "With the exception of search engine bots, the use of data-mining/extraction software or bots
> by any company that is not collecting data for a search engine is **strictly forbidden**."
> — 또한 "limited license to access and **make personal use** of this site and **not to download or modify it**"

`robots.txt`는 `/facts/`를 막지 않고 페이지도 익명으로 열린다. 하지만 **robots.txt는 크롤러 예절이고
구속력이 있는 것은 약관이다** — 둘이 어긋나면 약관이 이긴다. 우리가 하려는 일(자동 추출 + 서버 DB에
원문 저장)이 금지 문구 그대로다. Musixmatch 때는 기술적 장벽이 이유였지만 여기는 문서로 명시돼 있다.

같은 성격(사람이 쓴 곡 해설)을 **Genius가 무료 공식 API로 제공**하고 이미 붙어 있다 —
약관을 어길 실익이 없다. 정말 필요해지면 정당한 경로는 **Songfacts API 견적 문의**이고,
기존 구조(`ISongMeaningSource` + 소스 선택 옵션) 그대로 `SongfactsSource`를 더하면 된다.

대안으로 TheAudioDB(무료 API)도 확인했는데, `strDescriptionEN` 필드는 있으나 `Even Flow`에서
비어 있었다 — 곡 단위 해설 커버리지가 얇아 Genius를 대체하지 못한다.

### 3. 번역이 아니라 요약이다 — LLM을 쓴다

세 소스 모두 영어 산문이다. DeepL은 번역만 하므로 그대로 넣으면 "의미"가 아니라 긴 영어
문서의 긴 한국어판이 나온다. 그래서 요약이 가능한 LLM을 쓰되, 엔진을 **갈아끼울 수 있게**
`IMeaningWriter` + `MeaningWriterRegistry`로 감쌌다(기존 `ITranslator`/`TranslatorRegistry`와 같은 모양).

- **기본은 Google Gemini Developer API 직결.** Vertex AI가 아닌 이유는 인증이 API 키 한 줄이라
  이미 쓰는 `GoogleTranslateTranslator`와 패턴이 같아서다(서비스 계정·ADC 불필요).
  IAM·데이터 레지던시가 필요해지면 Vertex로 옮긴다.
  요금은 어느 쪽이든 부담이 없다 — 보유 곡 전체를 채워도 유료 기준 몇백 원이다.
  다만 **"$300 무료 체험 크레딧"은 Gemini API에 쓸 수 없다**(공식 문서의 명시적 제외 항목).
  진짜 무료로 가려면 별개 제도인 "무료 티어"를 써야 하고, 그건 **결제가 연결되지 않은
  프로젝트에만** 적용된다 — 결제를 붙이는 순간 Tier 1(유료)이 되고 무료 티어는 사라진다.
  가사 번역용 프로젝트는 Cloud Translation 때문에 결제가 필요하므로 **그 프로젝트를 그대로
  쓰면 유료다.** 무료를 원하면 결제 없는 별도 프로젝트가 필요하다(계정당 프로젝트 수 한도에
  걸릴 수 있다). 유료 티어는 대신 보낸 내용이 학습에 쓰이지 않는다.
- **OpenRouter를 함께 둔다.** OpenAI 호환 엔드포인트라 키 하나로 Claude·GPT·Gemini·Llama를
  `model` 문자열만 바꿔 부를 수 있다. 같은 곡을 여러 모델로 만들어 문장 품질을 비교할 때 쓴다.
- 둘 다 순수 HttpClient + System.Text.Json — SDK 의존성을 늘리지 않는다.

### 4. 생성은 사람이 누를 때만 — 자동 생성을 두지 않는다

새 가사가 올라올 때 자동으로 만들지 않는다. 관리자 화면의 단건 버튼과 일괄 백필만 둔다.

- 쿼타·비용이 예측 가능하다(무료 티어 한도를 모르게 긁지 않는다).
- 실패가 조용히 쌓이지 않는다.
- 광고·오인식 트랙까지 토큰을 쓰지 않는다.

결과는 실패·자료없음도 행으로 남긴다 — 백필을 다시 눌러도 같은 곡을 무한히 재시도하지 않는다.

**단 일시적 실패는 남기지 않는다.** 429(쿼타)와 5xx·타임아웃은 시간이 지나면 풀리는데, 이걸
`failed` 행으로 굳히면 한도가 회복된 뒤에도 그 곡은 영영 건너뛰어진다. 그래서 엔진은
"영구 실패"와 "일시적 실패"를 갈라 돌려주고(`MeaningWriteResult.Retryable`), 후자는 **아무것도
저장하지 않고** 백필이 그 자리에서 멈춘다 — 계속 돌아 봐야 남은 곡도 같은 벽에 부딪힐 뿐이고,
멈춰도 망가지는 것이 없다. 무료 티어처럼 분당 한도가 빡빡한 환경에서는
`MUSEBASE_MEANING_BACKFILL_DELAY_MS`로 호출 간격을 줄 수 있다(유료 티어는 필요 없어 기본 0).

### 5. 근거가 없으면 부르지 않고, 확신이 없으면 포기한다

곡 해설은 **그럴듯한 창작이 특히 쉬운 영역**이다. 두 가지 방어를 뒀다.

- 소스가 하나도 없으면 LLM을 **아예 호출하지 않는다**(`status='no-source'`).
- Wikipedia 문서 선택은 제목 일치와 아티스트 확인을 **필수 조건**으로 걸고, 못 채우면 포기한다.
  실측으로 걸린 함정: "(song)"이 붙은 제목을 무조건 우선했더니 `Kids / MGMT`에서 정답인
  `Kids (MGMT song)`("(song)"이 아니라 "(MGMT song)"이다)을 제치고 상위에 섞여 있던
  `Pursuit of Happiness (song)`이 뽑혔다. **엉뚱한 문서는 자료가 없는 것보다 나쁘다** —
  그럴듯하고 완전히 틀린 의미가 만들어지기 때문이다.
- 프롬프트도 "자료에 없는 내용은 지어내지 않는다 / 부족하면 부족하다고 쓴다"로 못을 박는다.

### 6. 출처 표기는 의무다

Wikipedia 본문은 CC BY-SA이고 Genius·Last.fm도 링크 표기를 요구한다. 요약을 보여 주는 화면은
출처 이름·링크를 함께 렌더해야 하며, 이를 위해 `/v1/meaning` 응답에 `attribution`을 싣는다
(원문 전체는 무거워 응답에서 비운다).

### 7. 저장은 별도 테이블

`meanings` 테이블을 새로 만들고(`PRAGMA user_version = 2`) 가사 테이블은 건드리지 않는다.
의미가 없어도 가사는 멀쩡해야 하고, 재생성이 가사 `revision`을 올리면 안 된다.
조회 키는 **가사와 같은 해석기**(`Locate`)를 쓴다 — 가사가 느슨한 키로 맞는 곡은 의미도 맞아야
앱에서 "가사는 뜨는데 의미만 빈" 상태가 생기지 않는다.

## 결과

- 앱에는 LLM 키를 심지 않는다. 서버가 대행하고 앱은 `/v1/meaning`을 읽기만 한다 —
  ADR-0005가 v2로 예고한 "서버가 번역 대행"과 같은 방향이다.
- 키를 하나도 넣지 않으면 기능이 통째로 꺼지고 외부 링크만 남는다. 가사 기능에는 영향이 없다.
- 실측으로 잡은 함정 하나 더: **Wikimedia는 User-Agent가 없으면 403을 준다.**
  .NET `HttpClient`는 기본 User-Agent를 보내지 않으므로 그대로 두면 위키피디아 소스가 항상
  조용히 빈다. 가사 제공자들의 검증된 동작을 건드리지 않도록 의미 전용 `MeaningHttp`에만 붙였다.

## 대안 (기각)

- **DeepL로 번역만** — 새 키가 필요 없지만 요약이 안 돼 긴 영어 bio가 긴 한국어 글이 될 뿐이고,
  가사 번역용 무료 할당까지 함께 먹는다.
- **Musixmatch 크롤링** — 약관 위반이고 Cloudflare로 막혀 있다.
- **Vertex AI** — 거버넌스가 필요할 때의 선택지다. 개인 프로젝트에는 서비스 계정·IAM 배선이 과하다.
