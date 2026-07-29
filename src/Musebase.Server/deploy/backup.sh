#!/usr/bin/env bash
# 가사 서버 DB 백업.
#
# WAL 모드에서는 cp가 안전하지 않으므로 sqlite3의 .backup(일관 스냅샷)을 쓴다.
# 뜬 스냅샷은 곧바로 integrity_check로 검증한 뒤에만 보관한다 — 깨진 백업을 모아 두면 없는 것만 못하다.
#
# 실행:
#   베어메탈  sudo /usr/local/bin/musebase-backup           (systemd timer가 매일 호출)
#   컨테이너  docker exec musebase-server /app/backup.sh    (호스트 timer가 호출)
#
# 환경변수:
#   MUSEBASE_DB              원본 DB (기본 /var/lib/musebase/lyrics.db)
#   MUSEBASE_BACKUP_DIR      보관 폴더 (기본 /var/backups/musebase)
#   MUSEBASE_BACKUP_KEEP_DAYS  보관 일수 (기본 14)
#   MUSEBASE_BACKUP_REMOTE   있으면 스냅샷을 이 대상으로도 복사한다(오프사이트).
#                            예) ubuntu@mini:/srv/backup/musebase  — 테일넷 이름이면 어디서든 붙는다.
set -euo pipefail

DB="${MUSEBASE_DB:-/var/lib/musebase/lyrics.db}"
DEST="${MUSEBASE_BACKUP_DIR:-/var/backups/musebase}"
KEEP_DAYS="${MUSEBASE_BACKUP_KEEP_DAYS:-14}"
REMOTE="${MUSEBASE_BACKUP_REMOTE:-}"

[ -f "$DB" ] || { echo "DB가 없습니다: $DB" >&2; exit 1; }
mkdir -p "$DEST"

STAMP="$(date +%F)"
SNAPSHOT="$DEST/lyrics-$STAMP.db"

# 1) 일관 스냅샷
sqlite3 "$DB" ".backup '$SNAPSHOT'"

# 2) 검증 — 깨졌으면 남기지 않는다(다음 실행이 성한 사본을 다시 만든다)
CHECK="$(sqlite3 "$SNAPSHOT" 'PRAGMA integrity_check;' | head -1)"
if [ "$CHECK" != "ok" ]; then
    echo "무결성 검사 실패($CHECK) — 스냅샷을 폐기합니다: $SNAPSHOT" >&2
    rm -f "$SNAPSHOT"
    exit 1
fi

SONGS="$(sqlite3 "$SNAPSHOT" 'SELECT COUNT(*) FROM lyrics;')"

# 3) 압축(가사는 텍스트라 1/4 이하로 줄어든다). 같은 날 재실행이면 덮어쓴다.
gzip -f "$SNAPSHOT"
ARCHIVE="$SNAPSHOT.gz"

# 4) 오프사이트 사본(선택) — 실패해도 로컬 백업은 유효하므로 경고만 남긴다
if [ -n "$REMOTE" ]; then
    if scp -q -o BatchMode=yes -o ConnectTimeout=10 "$ARCHIVE" "$REMOTE/"; then
        echo "원격 사본: $REMOTE/$(basename "$ARCHIVE")"
    else
        echo "경고: 원격 사본 실패($REMOTE) — 로컬 백업은 정상입니다" >&2
    fi
fi

# 5) 보존 기간 지난 것 정리
find "$DEST" -name 'lyrics-*.db.gz' -mtime "+$KEEP_DAYS" -delete

echo "백업 완료: $ARCHIVE ($SONGS곡, $(du -h "$ARCHIVE" | cut -f1))"
