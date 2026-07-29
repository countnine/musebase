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
EOF
sudo chmod 600 /etc/musebase/server.env     # 토큰 파일은 절대 저장소에 커밋하지 않는다
```

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

`/etc/systemd/system/musebase-backup.service`(Type=oneshot, ExecStart=/usr/local/bin/musebase-backup)와
`musebase-backup.timer`(OnCalendar=*-*-* 04:00:00)를 만들어 `systemctl enable --now musebase-backup.timer`.

WAL 모드에서는 `cp`가 안전하지 않으므로 스크립트는 `sqlite3 .backup`을 쓴다. 주 1회 개발 PC로
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
> 미스 목록은 그만큼 비게 된다).

## 9. 앱 설정

- **Windows**: 설정 → [소스] → "개인 가사 서버" 에 `https://oracle.<tailnet>.ts.net`과 토큰 입력.
- **Android**: 설정 → [재생 소스] 탭 아래쪽 "개인 가사 서버"에 같은 값 입력.

두 앱 모두 **저장 즉시 반영**되며(재시작 불필요), 서버에 못 붙으면 조용히 기존 동작
(로컬 캐시 → 제공자 검색)으로 강등된다.

## 업데이트

3~4단계를 반복하면 된다(`systemctl restart musebase-server`). DB는 `/var/lib/musebase`에
따로 있으므로 배포로 지워지지 않는다.
