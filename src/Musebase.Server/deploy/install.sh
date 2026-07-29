#!/usr/bin/env bash
# 가사 서버 최초 설치 — 서버(Oracle)에서 한 번 실행한다.
# 전제: /tmp/musebase-server/ 에 publish 산출물이 이미 올라와 있다(deploy/README.md 3단계).
#
#   sudo bash install.sh
#
# 하는 일: 사용자·디렉터리 생성 → 토큰 생성(없을 때만) → 바이너리 배치 → systemd 등록 →
#          tailscale serve로 HTTPS 노출 → 백업 스크립트·타이머 등록.
set -euo pipefail

STAGE="${STAGE:-/tmp/musebase-server}"
APP_DIR=/opt/musebase
DATA_DIR=/var/lib/musebase
ENV_FILE=/etc/musebase/server.env
PORT="${PORT:-5180}"

[[ $EUID -eq 0 ]] || { echo "sudo로 실행하세요."; exit 1; }
[[ -x "$STAGE/Musebase.Server" || -f "$STAGE/Musebase.Server" ]] || {
  echo "$STAGE 에 publish 산출물이 없습니다(deploy/README.md 3단계 먼저)."; exit 1; }

id musebase &>/dev/null || useradd --system --no-create-home musebase
mkdir -p "$APP_DIR" "$DATA_DIR" /etc/musebase
chown musebase:musebase "$DATA_DIR"

# 토큰은 한 번만 만든다(이미 있으면 유지 — 앱에 넣어 둔 값이 깨지지 않게).
if [[ ! -f "$ENV_FILE" ]]; then
  TOKEN=$(openssl rand -base64 32)
  cat > "$ENV_FILE" <<EOF
MUSEBASE_TOKEN=$TOKEN
MUSEBASE_DB=$DATA_DIR/lyrics.db
EOF
  chmod 600 "$ENV_FILE"
  echo "새 토큰을 만들었습니다. 앱 설정에 넣으세요:"
  echo "  $TOKEN"
else
  echo "기존 $ENV_FILE 을 그대로 씁니다(토큰 유지)."
fi

systemctl stop musebase-server 2>/dev/null || true
cp -r "$STAGE"/* "$APP_DIR"/
chmod +x "$APP_DIR/Musebase.Server"
chown -R musebase:musebase "$APP_DIR"

cp "$APP_DIR/deploy/musebase-server.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now musebase-server

# 단일 파일 배포는 첫 실행에 압축을 풀어 몇 초 걸린다 — 뜰 때까지 기다렸다 확인한다.
for _ in $(seq 1 15); do
  if curl -fsS "http://127.0.0.1:$PORT/v1/healthz" >/dev/null 2>&1; then
    echo "healthz ok"
    break
  fi
  sleep 1
done

# 테일넷에 HTTPS로 노출(공개 인터넷 노출인 funnel은 쓰지 않는다).
tailscale serve --bg --https=443 "http://127.0.0.1:$PORT"
tailscale serve status || true

# 백업(매일 04:00, 14일 보관)
install -m 755 "$APP_DIR/deploy/backup.sh" /usr/local/bin/musebase-backup
cat > /etc/systemd/system/musebase-backup.service <<'EOF'
[Unit]
Description=Musebase lyrics DB backup
[Service]
Type=oneshot
ExecStart=/usr/local/bin/musebase-backup
EOF
cat > /etc/systemd/system/musebase-backup.timer <<'EOF'
[Unit]
Description=Daily Musebase lyrics DB backup
[Timer]
OnCalendar=*-*-* 04:00:00
Persistent=true
[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now musebase-backup.timer

echo
echo "설치 완료. 앱 설정에 넣을 주소:"
tailscale status --json | grep -o '"DNSName":"[^"]*"' | head -1 | sed 's/.*"DNSName":"\(.*\)\."/  https:\/\/\1/'
echo "토큰은 $ENV_FILE 안에 있습니다: sudo grep MUSEBASE_TOKEN $ENV_FILE"
