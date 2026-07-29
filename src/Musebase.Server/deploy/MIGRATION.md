# 가사 서버 옮기기 · 백업 · 복구

서버가 갖고 있는 상태는 **SQLite 파일 하나(`lyrics.db`)와 토큰**이 전부다. 그래서 이전은
"파일 하나 옮기고 서버를 다시 띄우는 일"로 끝난다. 앱 설정도 바꿀 필요가 없다 —
주소(`https://<호스트>.<tailnet>.ts.net`)와 토큰을 그대로 유지하면 기기들은 옮긴 줄도 모른다.

## 지금 상태 (Oracle, 베어메탈)

| | |
|---|---|
| 실행 | systemd `musebase-server` (self-contained 단일 파일, .NET 설치 불필요) |
| DB | `/var/lib/musebase/lyrics.db` |
| 설정 | `/etc/musebase/server.env` (토큰·기기 매핑) |
| 노출 | `tailscale serve --https=443 → 127.0.0.1:5180` |
| 백업 | `musebase-backup.timer` 매일 04:00 → `/var/backups/musebase/lyrics-YYYY-MM-DD.db.gz` (14일) |

## 백업

`deploy/backup.sh`가 하는 일 — **`cp`가 아니라 `sqlite3 .backup`**(WAL 중에도 일관 스냅샷) →
**`PRAGMA integrity_check`로 검증**(깨졌으면 남기지 않는다) → gzip → 보존 기간 정리.

```bash
sudo /usr/local/bin/musebase-backup          # 수동 1회
systemctl list-timers musebase-backup.timer  # 다음 실행 확인
journalctl -u musebase-backup -n 20          # 결과 확인
```

### 오프사이트 사본 (권장)

한 대에만 두면 그 기계가 죽을 때 같이 죽는다. `/etc/musebase/server.env`에 한 줄이면 된다:

```
MUSEBASE_BACKUP_REMOTE=ubuntu@mini:/srv/backup/musebase
```

테일넷 이름을 쓰면 어디에 있든 붙는다. 대상 호스트에 이 서버의 공개키를 등록해 두면
(`ssh-copy-id`) 매일 백업이 끝난 뒤 자동으로 한 부 더 넘어간다. 실패해도 로컬 백업은 그대로다.

> 백업 파일에는 가사 전문과 조회 기록(청취 이력)이 들어 있다. 보관 위치도 개인 범위로 유지한다.

### 복구

```bash
sudo systemctl stop musebase-server
sudo gunzip -c /var/backups/musebase/lyrics-2026-07-29.db.gz | sudo tee /var/lib/musebase/lyrics.db >/dev/null
sudo rm -f /var/lib/musebase/lyrics.db-wal /var/lib/musebase/lyrics.db-shm   # 옛 WAL 잔재 제거
sudo chown musebase:musebase /var/lib/musebase/lyrics.db
sudo systemctl start musebase-server
curl -H "Authorization: Bearer $TOKEN" https://<호스트>.<tailnet>.ts.net/v1/stats   # 곡 수 확인
```

---

## 다른 서버로 옮기기

Docker로 옮기는 것을 권한다. 이미 immich를 컨테이너로 돌리고 있다면 같은 방식으로 관리되고,
호스트에 .NET을 설치할 필요가 없으며, 다음 이사 때도 이미지 태그만 맞추면 된다.
(베어메탈로 옮기려면 `README.md`의 3~5단계를 새 호스트에서 반복하면 된다 — 이 문서의 "파일 옮기기"만 따르면 된다.)

### 1. 새 호스트 준비

```bash
sudo tailscale up                 # 같은 테일넷에 넣는다
sudo tailscale set --ssh          # (선택) 관리 편의
```

### 2. 이미지 빌드 · 기동

```bash
git clone https://github.com/countnine/musebase && cd musebase
cd src/Musebase.Server/deploy
cat > .env <<'EOF'
MUSEBASE_TOKEN=<기존 서버와 같은 토큰>
MUSEBASE_DEVICES=100.94.166.60=home-pc,100.67.96.56=s26
MUSEBASE_TZ=Asia/Seoul
EOF
chmod 600 .env
cd ../../..
docker compose -f src/Musebase.Server/deploy/docker-compose.yml up -d --build
```

**토큰을 기존 값 그대로 쓰는 것이 핵심**이다. 그러면 기기 설정을 하나도 건드리지 않아도 된다.

### 3. 데이터 옮기기

```bash
# 옛 서버에서
sudo /usr/local/bin/musebase-backup
sudo scp /var/backups/musebase/lyrics-$(date +%F).db.gz newhost:/tmp/

# 새 호스트에서
docker compose -f src/Musebase.Server/deploy/docker-compose.yml stop musebase-server
gunzip -c /tmp/lyrics-*.db.gz > /tmp/lyrics.db
docker cp /tmp/lyrics.db musebase-server:/data/lyrics.db
docker compose -f src/Musebase.Server/deploy/docker-compose.yml start musebase-server
docker exec musebase-server sqlite3 /data/lyrics.db "PRAGMA integrity_check; SELECT COUNT(*) FROM lyrics;"
```

### 4. 노출 전환 (다운타임 ~1분)

```bash
# 새 호스트
sudo tailscale serve --bg --https=443 http://127.0.0.1:5180
curl https://<새호스트>.<tailnet>.ts.net/v1/healthz     # ok

# 옛 서버 — 노출을 내려 두 대가 동시에 응답하지 않게 한다
sudo tailscale serve --https=443 off
sudo systemctl disable --now musebase-server
```

주소가 바뀌므로 **각 기기의 설정에서 서버 주소만 새 호스트로 고친다**(토큰은 그대로).
Windows 설정 → [소스], Android 설정 → [재생 소스].

> 주소를 아예 바꾸고 싶지 않다면: 옛 호스트의 tailnet 이름을 새 기계에 물려주는 방법이 있다
> (관리 콘솔에서 옛 노드 삭제 후 새 노드 이름을 같게 지정). 그러면 앱 설정도 손대지 않아도 된다.

### 5. 백업 타이머 (새 호스트)

컨테이너 안에 스케줄러를 두지 않고 호스트 타이머가 `docker exec`로 부른다.

```bash
sudo tee /etc/systemd/system/musebase-backup.service >/dev/null <<'EOF'
[Unit]
Description=Musebase lyrics DB backup (docker)
[Service]
Type=oneshot
ExecStart=/usr/bin/docker exec musebase-server /app/backup.sh
EOF
sudo tee /etc/systemd/system/musebase-backup.timer >/dev/null <<'EOF'
[Unit]
Description=Daily Musebase lyrics DB backup
[Timer]
OnCalendar=*-*-* 04:00:00
Persistent=true
[Install]
WantedBy=timers.target
EOF
sudo systemctl daemon-reload && sudo systemctl enable --now musebase-backup.timer
docker exec musebase-server /app/backup.sh    # 즉시 1회 확인
```

백업은 `musebase-backups` 볼륨에 쌓인다. 호스트에서 꺼내려면
`docker cp musebase-server:/backups /srv/backup/musebase` 또는 볼륨을 바인드 마운트로 바꾼다.

### 6. 업데이트

```bash
git pull
docker compose -f src/Musebase.Server/deploy/docker-compose.yml up -d --build
```

DB는 볼륨에 있으므로 재빌드로 지워지지 않는다. 스키마는 `PRAGMA user_version` 기반으로
기동 때 자동 마이그레이션된다.

## Docker로 갈 때 알아 둘 것

- **바인드 마운트를 쓰면** 컨테이너가 비루트(uid 1654)로 돌기 때문에 `sudo chown -R 1654:1654 <경로>`가 필요하다.
  이름 있는 볼륨(기본 설정)은 도커가 알아서 맞춰 준다.
- **포트는 `127.0.0.1:5180`에만 바인딩**한다. 공개 노출은 `tailscale serve`가 전담하고, 방화벽은 건드리지 않는다.
- 컨테이너 안에서 `X-Forwarded-For`가 그대로 보인다(tailscale serve → 호스트 루프백 → 컨테이너).
  기기 이름 매핑(`MUSEBASE_DEVICES`)은 그대로 쓰면 된다.
- 이미지에 `tzdata`를 넣어 두었다 — 없으면 관리자 화면 시간이 UTC로 강등된다.
- immich와 같은 호스트에 둬도 자원 다툼은 사실상 없다(가사 서버는 idle 40~90MB, DB 2MB 수준).
