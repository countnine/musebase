# 가사 서버 배포 (Oracle + Tailscale)

개인용 가사 캐시 서버를 Tailscale 테일넷 안에서만 접근 가능하게 띄운다.
공개 인터넷에 여는 포트는 **없다** — 앱은 루프백만 리슨하고 노출은 `tailscale serve`가 담당한다.

API 계약은 `contracts/lyrics-api.md`(v1).

## 1. 토큰 만들기

```bash
openssl rand -base64 32
```

## 2. 서버 준비 (Oracle, 최초 1회)

```bash
sudo useradd --system --no-create-home musebase
sudo mkdir -p /opt/musebase /var/lib/musebase /etc/musebase
sudo chown musebase:musebase /var/lib/musebase

sudo tee /etc/musebase/server.env >/dev/null <<'EOF'
MUSEBASE_TOKEN=여기에_위에서_만든_토큰
MUSEBASE_DB=/var/lib/musebase/lyrics.db
# --- 관리자 페이지(선택) ---
# MUSEBASE_ADMIN_TOKEN=별도_토큰      # 생략하면 MUSEBASE_TOKEN을 그대로 쓴다
# MUSEBASE_DEVICES=100.1.2.3=거실PC,100.4.5.6=갤럭시   # 첫 배포 후 진단 섹션에서 실제 IP 확인해 채운다
# MUSEBASE_TZ=Asia/Seoul             # 화면 표시 시간대(기본값)
# MUSEBASE_LOG_LOOKUPS=0             # 조회 기록을 남기지 않으려면
# MUSEBASE_LOOKUP_RETENTION_DAYS=90  # 조회 기록 보존 기간(기본 90일)
# MUSEBASE_YIELD_WINDOW_SECONDS=30   # 번역 양보 판정 창(0이면 끔) — 아래 참고
# --- 곡의 의미(선택) — 11절 참고 ---
# MUSEBASE_MEANING_ENGINE=gemini     # gemini | openrouter | none(기본)
# MUSEBASE_GEMINI_API_KEY=...
# MUSEBASE_GENIUS_TOKEN=...
# MUSEBASE_LASTFM_KEY=...
# --- Last.fm 좋아요(선택) — 12절 참고 ---
# MUSEBASE_LASTFM_SECRET=...         # 있어야 좋아요를 켜고 끌 수 있다(읽기는 KEY만으로 된다)
EOF
sudo chmod 600 /etc/musebase/server.env     # 토큰 파일은 절대 저장소에 커밋하지 않는다
```

### 관리자 로그인 — 아이디·비밀번호

기본은 토큰 로그인이다(주소창에 `?token=…`). 기기가 여러 대면 긴 토큰을 매번 붙여 넣어야 해
불편하므로, 아이디·비밀번호를 정할 수 있다.

```bash
# 설정 파일에 평문을 두지 않도록 해시를 만든다
/opt/musebase/Musebase.Server --hash-password '정할비밀번호'
# → pbkdf2$210000$…$…
```

```
MUSEBASE_ADMIN_USER=admin              # 생략하면 admin
MUSEBASE_ADMIN_PASSWORD=pbkdf2$210000$…$…
```

- 값이 `pbkdf2$`로 시작하면 해시로, 아니면 **평문 그대로** 비교한다. 평문도 동작하지만
  비밀번호는 다른 서비스와 돌려 쓰이기 쉬워, 설정 파일이 한 번 새면 피해가 여기서 끝나지 않는다
  — 해시를 권한다.
- **토큰 로그인은 계속 살아 있다.** 비밀번호를 잊거나 해시를 잘못 넣어도 들어갈 수 있어야 하기
  때문이다(로그인 화면의 "토큰으로 들어가기"). 토큰은 어차피 앱이 API에 쓰는 값이라 새 비밀이 늘지 않는다.
- 비밀번호를 지우고 재시작하면 예전처럼 토큰 화면만 나온다.

## 3. 빌드 · 전송 (개발 PC)

Oracle 무료 티어는 보통 **Ampere A1(ARM64)** 이다. x86 인스턴스면 `linux-x64`로 바꾼다.

```powershell
dotnet publish src/Musebase.Server/Musebase.Server.csproj -c Release -r linux-arm64 `
  --self-contained true -p:PublishSingleFile=true -o publish-server
scp -r publish-server/* oracle:/tmp/musebase-server/
```

```bash
# 서버에서
sudo systemctl stop musebase-server 2>/dev/null || true
sudo cp -r /tmp/musebase-server/* /opt/musebase/
sudo chmod +x /opt/musebase/Musebase.Server
sudo chown -R musebase:musebase /opt/musebase
```

self-contained라 서버에 .NET 런타임을 설치하거나 갱신할 필요가 없다. 트리밍은 켜지 않는다
(SQLite provider·System.Text.Json 리플렉션이 깨질 수 있고, 크기 이득이 의미 없다).

## 4. systemd 등록

```bash
# 아래 4~7단계는 deploy/install.sh 한 방으로도 됩니다: sudo bash /tmp/musebase-server/deploy/install.sh
sudo cp /opt/musebase/deploy/musebase-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now musebase-server
journalctl -u musebase-server -f
```

## 5. Tailscale로 HTTPS 노출

```bash
sudo tailscale serve --bg --https=443 http://127.0.0.1:5180
tailscale serve status      # https://oracle.<tailnet>.ts.net/ 확인
```

- 관리 콘솔에서 **MagicDNS**와 **HTTPS Certificates**를 켜 둬야 한다. 인증서는 Let's Encrypt로
  자동 발급·갱신되며 정규 신뢰 체인이라 앱에서 별도 설정이 필요 없다.
- **`tailscale funnel`은 쓰지 않는다** — 공개 인터넷 노출이다.
- Android는 평문 HTTP를 기본 차단하므로 앱에는 **반드시 `https://` 주소**를 넣는다.
  IP(`100.x.y.z`)로 접속하면 인증서 이름이 맞지 않아 실패한다 — MagicDNS 이름을 쓴다.

확인:

```bash
curl https://oracle.<tailnet>.ts.net/v1/healthz            # ok
curl -H "Authorization: Bearer $TOKEN" https://oracle.<tailnet>.ts.net/v1/stats
```

## 6. 기존 캐시로 시드 (선택)

이미 쌓아 둔 PC의 캐시를 그대로 올리면 서버가 즉시 유용해진다.

```powershell
scp $env:LOCALAPPDATA\Musebase\translations.db oracle:/tmp/
```

```bash
sudo systemctl stop musebase-server
sudo -u musebase MUSEBASE_DB=/var/lib/musebase/lyrics.db /opt/musebase/Musebase.Server --import /tmp/translations.db
sudo systemctl start musebase-server
```

병합 정책을 그대로 거치므로 사용자 편집본은 덮이지 않는다.

## 7. 백업

```bash
sudo cp /opt/musebase/deploy/backup.sh /usr/local/bin/musebase-backup
sudo chmod +x /usr/local/bin/musebase-backup
```

백업 스크립트는 `sqlite3 .backup`(WAL 중에도 일관 스냅샷) → `PRAGMA integrity_check` 검증 → gzip →
보존 기간 정리 순으로 동작한다. 자세한 내용과 **오프사이트 사본·복구 절차·다른 서버로 이전**은
`MIGRATION.md`를 참고한다.

`/etc/systemd/system/musebase-backup.service`(Type=oneshot, ExecStart=/usr/local/bin/musebase-backup)와
`musebase-backup.timer`(OnCalendar=*-*-* 04:00:00)를 만들어 `systemctl enable --now musebase-backup.timer`.

오프사이트 사본은 `/etc/musebase/server.env`에 `MUSEBASE_BACKUP_REMOTE=user@host:/path` 한 줄이면
매일 백업 뒤 자동으로 넘어간다(테일넷 이름 사용 가능). 주 1회 개발 PC로
`scp` 회수해 두면 오프사이트 백업이 된다.

## 8. 관리자 페이지

`https://oracle.<tailnet>.ts.net/admin?token=<관리자토큰>` 을 브라우저로 연다. 토큰이 맞으면
서명 쿠키를 굽고 **주소창을 `/admin`으로 정리**하므로 토큰이 히스토리에 남지 않는다(30일 유지,
`/admin/logout`으로 해제). 테일넷 전용이라 밖에서는 접근 자체가 불가능하다.

- **대시보드** — 마지막 조회·오늘 조회·7일 히트율·보관 곡 수 타일, 최근 조회 50건(히트/느슨한 히트/미스),
  미스 상위(서버에 없어 각 기기가 직접 찾은 곡 = 채울 후보), 기기별·일별 통계, 최근 올라온 가사,
  번역 없는 곡, 표기 차이로 갈린 곡 후보, 느슨한 키로 맞은 조회.
- **가사 검색** — 제목·아티스트 부분 일치 → 상세에서 원문·번역을 나란히 보고, 타임태그 토글·언어 선택·
  원문(.lrc) 내려받기. 상세에서 **편집**(저장하면 `origin=user`가 되어 각 기기의 자동 검색이 덮어쓰지 못한다)과
  **삭제**(다음 재생 때 어느 기기든 다시 찾아 새로 채운다)가 가능하다.

**기기 이름**: 첫 배포 직후에는 IP로만 보인다. 대시보드 하단 "진단" 섹션에서 실제로 들어오는
`X-Forwarded-For`/소스 IP를 확인해 `MUSEBASE_DEVICES=100.x.y.z=거실PC` 형식으로 넣고 재시작하면
사람이 읽는 이름으로 나온다.

> **프라이버시**: 조회 기록에는 곡명·아티스트·기기·시각이 남는다(기본 90일 보관 후 자동 삭제).
> 사실상 청취 이력이므로, 남기고 싶지 않으면 `MUSEBASE_LOG_LOOKUPS=0`으로 끈다(대시보드의 히트율·
> 미스 목록은 그만큼 비게 되고, 아래 **번역 양보**도 함께 꺼진다 — 판단 근거가 조회 기록이다).

## 10. 번역 양보 (동시 재생 중복 줄이기)

Spotify Connect처럼 **PC에서 재생하고 폰에서 조작**하면 두 기기가 같은 곡을 동시에 처리해 각자
유료 번역 API를 부른다. 서버는 조회가 미스일 때 최근 `MUSEBASE_YIELD_WINDOW_SECONDS`(기본 30초)
안에 **다른 기기**도 같은 제목을 미스했는지 보고, 그렇다면 404 본문에 힌트를 실어 준다:

```json
{ "error": "not found", "pending": true, "retryAfterMs": 3000 }
```

받은 앱은 제공자 검색은 그대로 하되(원문 가사 표시는 늦어지지 않는다) 번역만 잠시 미루고 서버를
재조회해, 먼저 끝낸 기기가 올린 번역본을 받아 쓴다. 계약 전문은 `contracts/lyrics-api.md`.

- `MUSEBASE_YIELD_WINDOW_SECONDS=0`이면 힌트를 주지 않는다(기능 끔).
- 구버전 앱은 이 필드를 무시하므로 서버만 새로 올려도 안전하다.
- 잘 도는지는 관리자 대시보드 "최근 업로드"로 본다 — 동시에 튼 곡을 **한 기기만** 올렸으면 성공이다.

## 9. 앱 설정

- **Windows**: 설정 → [소스] → "개인 가사 서버" 에 `https://oracle.<tailnet>.ts.net`과 토큰 입력.
- **Android**: 설정 → [재생 소스] 탭 아래쪽 "개인 가사 서버"에 같은 값 입력.

두 앱 모두 **저장 즉시 반영**되며(재시작 불필요), 서버에 못 붙으면 조용히 기존 동작
(로컬 캐시 → 제공자 검색)으로 강등된다.

## 11. 곡의 의미 (선택)

곡이 무엇에 대한 노래인지 한 문단으로 만들어 관리자 화면과 `/v1/meaning`에 실어 준다.
**키를 넣지 않으면 통째로 꺼지고** 곡 상세에 Musixmatch·Genius 링크만 남는다(가사 기능엔 영향 없음).

### 키 발급

| 키 | 어디서 | 비고 |
|---|---|---|
| `MUSEBASE_GEMINI_API_KEY` | <https://aistudio.google.com/apikey> | 요금은 아래 "무료로 쓰려면" 참고 |
| `MUSEBASE_GENIUS_TOKEN` | <https://genius.com/api-clients> → New API Client → **Generate Access Token** | 무료. OAuth 사용자 플로우 불필요 |
| `MUSEBASE_LASTFM_KEY` | <https://www.last.fm/api/account/create> | 선택. Genius에 설명이 없는 곡을 메워 준다. 같은 페이지의 Shared secret은 12절(좋아요)에서 쓴다 |
| `MUSEBASE_MUSIXMATCH_KEY` | <https://developer.musixmatch.com> | 선택. **곡 페이지 링크를 정확히** 만드는 데 쓴다(아래) |

Wikipedia는 키가 필요 없고 기본으로 켜져 있다(`MUSEBASE_MEANING_WIKIPEDIA=0`으로 끔).

```
MUSEBASE_MEANING_ENGINE=gemini              # gemini | openrouter | none(기본)
MUSEBASE_MEANING_LANG=ko
MUSEBASE_GEMINI_API_KEY=...
MUSEBASE_GEMINI_MODEL=gemini-2.5-flash-lite # 생략 가능
MUSEBASE_GENIUS_TOKEN=...
MUSEBASE_LASTFM_KEY=...
MUSEBASE_MUSIXMATCH_KEY=...                 # 선택 — 곡 페이지 링크 정확도
MUSEBASE_MEANING_SOURCES=genius,lastfm,wikipedia   # 기본값. musixmatch는 빠져 있다
MUSEBASE_MEANING_BACKFILL_LIMIT=50          # 일괄 생성 1회 처리량
MUSEBASE_MEANING_BACKFILL_DELAY_MS=0        # 호출 간 간격 — 무료 티어면 4500
```

### 자료원을 고른다 — `MUSEBASE_MEANING_SOURCES`

쉼표로 나열한다. 목록에 있고 **키까지 있는** 소스만 실제로 쓰인다(위키피디아만 키가 필요 없다).
지금 켜져 있는 자료원은 관리자 대시보드에 그대로 표시된다.

`musixmatch`는 **기본값에 없다.** 그 사이트의 "Meaning"은 사람이 쓴 해설이 아니라 가사를 기계로
분석한 결과이고(같은 블록에 무드·테마·콘텐츠 등급이 함께 온다), 자료로 넣으면 LLM이 쓴 글을 다시
LLM에 넣어 요약하는 셈이 된다. 켜면 출처가 `Musixmatch (AI 분석)`으로 표시되고, 프롬프트가
"다른 자료와 어긋나면 다른 자료를 따른다"로 취급한다. 스크래핑이라 약관 위험도 함께 진다 —
**켜는 판단은 운영자 몫이다.**

```
MUSEBASE_MEANING_SOURCES=genius,lastfm,wikipedia,musixmatch
```

### Musixmatch 링크

키를 넣으면 공식 API(`track.search`)로 확인한 **그 곡의 페이지**로 링크가 걸린다. 키가 없으면
검색 링크로 물러난다. 주소를 규칙으로 만들지 않는 이유는 실측 때문이다 —
`/lyrics/Pearl-Jam/Even-Flow`가 오류 없이 `/lyrics/Pearl-Jam/Alive`(**다른 곡**)로 넘어갔다.

### 무료로 쓰려면 — 헷갈리는 지점

**"$300 무료 체험 크레딧"과 "Gemini API 무료 티어"는 다른 제도다.** 크레딧은 Gemini API에
**쓸 수 없다**(Google 공식 문서의 명시적 제외 항목). 무료로 쓰는 길은 무료 티어 하나뿐이고,
그건 **결제 계정이 연결되지 않은 프로젝트에만** 적용된다.

여기서 함정: 결제를 연결하는 순간 그 프로젝트는 즉시 **Tier 1(유료)** 이 되고 무료 티어는
사라진다. 크레딧은 안 먹히므로 카드에서 실제로 청구된다. 되돌리려면 결제를 명시적으로 해제해야 한다.

- **무료로 가려면**: 결제가 없는 **별도 프로젝트**를 만들어 그 안에서 키를 발급한다.
  가사 번역용 프로젝트(Cloud Translation)는 결제가 필요하므로 **그쪽 결제를 끄면 안 된다.**
  무료 티어는 15 RPM이라 백필을 한 번에 돌리려면 `MUSEBASE_MEANING_BACKFILL_DELAY_MS=4500`을 준다.
- **유료(Tier 1)로 가도 된다**: 곡당 사실상 0원이라 보유 곡 전체를 채워도 몇백 원 수준이고,
  분당 한도가 넉넉해 간격이 필요 없다. 무료 티어와 달리 **보낸 내용이 학습에 쓰이지 않는다.**

쿼타에 걸려도 안전하다 — 429·5xx는 저장하지 않고 백필이 그 자리에서 멈춘다. 남은 곡은
손대지 않으므로 나중에 다시 누르면 이어서 진행된다(영구 실패만 행으로 남아 건너뛰어진다).

### 모델을 바꿔 보고 싶다면

`MUSEBASE_MEANING_ENGINE=openrouter` + `MUSEBASE_OPENROUTER_API_KEY`로 바꾸고
`MUSEBASE_OPENROUTER_MODEL`에 모델 문자열만 넣으면 된다(`anthropic/claude-opus-5`,
`google/gemini-2.5-flash` …). 같은 곡을 [다시 생성]으로 만들어 문장을 비교할 수 있다.
OpenRouter는 Google Cloud 프로젝트가 아예 필요 없어, 프로젝트 한도에 막혔을 때의 우회로이기도 하다.

### 쓰는 법

- 곡 상세 → **[의미 가져오기]** (다시 누르면 재생성)
- 대시보드 → **[의미 일괄 생성]** — 아직 안 해 본 곡을 상한까지 처리
- **생성은 사람이 누를 때만 일어난다.** 자동 생성은 두지 않았다 — 쿼타·비용이 예측 가능해야 하고
  실패가 조용히 쌓이면 안 되기 때문이다.

> **출처 표기 의무**: Wikipedia 본문은 CC BY-SA, Genius·Last.fm도 링크 표기를 요구한다.
> 관리자 화면과 `/v1/meaning`의 `attribution`이 이를 담고 있으므로, 요약을 보여 주는 화면은
> 출처를 함께 표시해야 한다.

## 12. Last.fm 좋아요 · 커버 이미지 (선택)

관리자 화면 전용이다. 앱에는 나가지 않으므로 이 절을 건너뛰어도 앱 동작은 그대로다.

### 곡 상세의 외부 링크

곡 상세 머리말 아래에 **Last.fm · Tunefind · YouTube · Musixmatch · Genius** 링크가 있다.
Tunefind는 이 곡이 어느 드라마·영화에 쓰였는지 보러 가는 곳인데 **API는 쓰지 않는다** —
셀프서비스 가입 창구가 없고 라이선스 계약이 필요하며 무료 티어가 없다(문의처 `info@tunefind.com`).

### 커버 이미지

키가 필요 없다. 곡 상세를 처음 열 때 iTunes Search API로 찾고(없으면 Deezer),
찾은 주소를 DB에 기억한다. **못 찾은 것도 기억하므로** 열 때마다 다시 부르지 않는다 —
곡명·아티스트를 고친 뒤에는 곡 상세 아래 **[커버 다시 찾기]** 를 누른다.

Last.fm은 앨범 이미지를 주지만 쓰지 않는다. API 약관이 artwork를 계약 대상에서 **명시적으로
제외**하기 때문이다(가져올 수 있다는 것과 써도 된다는 것은 다르다).

### Last.fm 좋아요

곡 상세에서 좋아요를 켜고 끌 수 있다. 읽기(좋아요 여부)는 `MUSEBASE_LASTFM_KEY`만으로 되고,
**켜고 끄려면 shared secret과 계정 연결이 더 필요하다.**

```
MUSEBASE_LASTFM_KEY=...       # API 계정 페이지의 API key
MUSEBASE_LASTFM_SECRET=...    # 같은 페이지의 Shared secret — 이게 있어야 연결 버튼이 뜬다
```

<https://www.last.fm/api/accounts> 에서 두 값을 함께 볼 수 있다. 넣고 재시작한 뒤:

1. 대시보드 → **[Last.fm 계정 연결]**
2. last.fm 승인 페이지에서 허용
3. 돌아오면 `연결됨: 아이디`가 뜬다

콜백 주소는 등록하지 않아도 된다 — 그때 접속한 주소를 그대로 넘긴다. 승인은 **브라우저에서**
일어나므로 테일넷 안 주소여도 문제없다.

> **세션 키는 DB(`app_settings`)에 저장된다.** 백업 파일에 Last.fm 쓰기 자격증명이 함께 들어간다는
> 뜻이다. 지우려면 대시보드의 **[Last.fm 연결 해제]**, 또는 last.fm 설정 > Applications에서
> 권한 자체를 회수한다.

## 업데이트

3~4단계를 반복하면 된다(`systemctl restart musebase-server`). DB는 `/var/lib/musebase`에
따로 있으므로 배포로 지워지지 않는다. 스키마는 `PRAGMA user_version`으로 자동 이행된다
(현재 7 = `lyrics` + `lookups` + `meanings` + `ad_titles` + `song_links` + `app_settings`).
컬럼·테이블 추가뿐이라 **구 버전 바이너리로 롤백해도 안전하다.**
