#!/usr/bin/env bash
# 가사 서버 DB 백업. WAL 모드에서는 cp가 안전하지 않으므로 sqlite3의 .backup을 쓴다.
# systemd timer로 매일 04:00 실행하는 것을 권장한다(deploy/README.md 참고).
set -euo pipefail

DB="${MUSEBASE_DB:-/var/lib/musebase/lyrics.db}"
DEST="${MUSEBASE_BACKUP_DIR:-/var/backups/musebase}"
KEEP_DAYS="${MUSEBASE_BACKUP_KEEP_DAYS:-14}"

mkdir -p "$DEST"
sqlite3 "$DB" ".backup '$DEST/lyrics-$(date +%F).db'"
find "$DEST" -name 'lyrics-*.db' -mtime "+$KEEP_DAYS" -delete

echo "백업 완료: $DEST/lyrics-$(date +%F).db"
