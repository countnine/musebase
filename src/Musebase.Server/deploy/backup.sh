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
#   MUSEBASE_BACKUP_REMOTE   있으면 스냅샷을 이 대상들로도 복사한다(오프사이트). 여러 곳이면
#                            공백이나 쉼표로 구분한다 — 성격이 다른 두 곳(클라우드 + 집 기기)을
#                            두면 한쪽이 죽어도 사본이 남는다. 대상마다 형식:
#                            ubuntu@mini:/srv/backup/musebase  — scp(테일넷 이름이면 어디서든 붙는다)
#                            gs://버킷/musebase                — GCS(아래 서비스 계정 키로 직접 업로드)
#
#   MUSEBASE_BACKUP_PASSPHRASE  gs:// 대상에 올리는 사본을 암호화할 비밀번호(gpg AES-256). **필수** —
#                            백업에는 관리화면에서 넣은 API 키·Last.fm/Spotify 토큰이 평문으로 들어 있어
#                            비밀번호 없이는 클라우드에 올리지 않는다. 테일넷 기기(scp) 사본은 암호화하지 않는다.
#   MUSEBASE_BACKUP_GCS_KEY  GCS 쓰기용 서비스 계정 키 파일 경로(예: /etc/musebase/gcs-backup-key.json).
#                            gs:// 대상이 있으면 필수. python3(표준 라이브러리)와 openssl만 쓴다 — gcloud는 필요 없다.
#
# gcloud를 쓰지 않는 이유: `gcloud storage cp`는 올리기 전에 버킷 목록(objects.list)과 대상 객체
# (objects.get)를 먼저 읽는다. 백업 계정에는 일부러 "만들기"만 주므로 그 확인에서 막힌다(실측).
# 업로드 API를 직접 부르면 만들기 권한 하나로 충분하고, 1GB VM에서 무거운 gcloud를 띄우지도 않는다.
#
# GCS 사본은 이름에 시각까지 넣는다(lyrics-2026-09-18-040003.db.gz.gpg). 서비스 계정에
# "만들기"만 주고 덮어쓰기·지우기를 안 줘도 되게 하려는 것이다 — 서버가 털려도 클라우드
# 사본은 지울 수 없다. 보존 기간은 버킷 수명 주기 규칙이 정한다.
#
# 원격 복사가 **하나라도** 실패하면 나머지 대상과 로컬 정리까지 마친 뒤 **비정상 종료**한다 —
# systemd에 failed로 남아야 알아챌 수 있다. 예전에는 경고만 찍고 성공으로 끝나, 오프사이트
# 사본이 없는 줄 몰랐다.
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

# 4) 오프사이트 사본(선택). 실패해도 로컬 백업은 유효하므로 정리까지 마친 뒤 비정상 종료한다.
REMOTE_FAILED=0
SEALED=""   # GCS용 암호화 사본 — 대상이 여럿이어도 한 번만 만든다
# if로 쓴다 — `[ -n ] && rm`은 지울 게 없을 때 1을 남기고, EXIT trap의 마지막 상태가
# 스크립트 종료 코드를 덮어써 원격 대상이 없는 평범한 백업까지 failed가 됐다.
cleanup() { if [ -n "$SEALED" ]; then rm -f "$SEALED"; fi; }
trap cleanup EXIT

# 클라우드로 나가는 사본만 암호화한다. 비밀번호는 명령줄(ps에 보임)이 아니라 fd로 넘긴다.
seal() {
    [ -n "$SEALED" ] && return 0
    [ -n "${MUSEBASE_BACKUP_PASSPHRASE:-}" ] || {
        echo "MUSEBASE_BACKUP_PASSPHRASE가 없어 클라우드에 올리지 않습니다(평문 키가 들어 있다)" >&2
        return 1; }
    SEALED="$DEST/lyrics-$STAMP-$(date +%H%M%S).db.gz.gpg"
    gpg --batch --yes --quiet --pinentry-mode loopback --passphrase-fd 3 \
        --symmetric --cipher-algo AES256 -o "$SEALED" "$ARCHIVE" 3<<<"$MUSEBASE_BACKUP_PASSPHRASE" \
        || { rm -f "$SEALED"; SEALED=""; return 1; }
}

# GCS 업로드 — 서비스 계정 키로 JWT를 서명해 토큰을 받고, 업로드 API에 한 번 보낸다.
# ifGenerationMatch=0: 같은 이름이 있으면 덮어쓰지 않고 실패한다(만들기 전용 계정과 같은 뜻).
# 응답의 크기·MD5를 로컬 파일과 대조한다 — 읽기 권한 없이도 제대로 올라갔는지 확인하는 방법이다.
gcs_put() {  # $1 파일, $2 gs://버킷/경로
    [ -n "${MUSEBASE_BACKUP_GCS_KEY:-}" ] || { echo "MUSEBASE_BACKUP_GCS_KEY가 없습니다" >&2; return 1; }
    python3 - "$MUSEBASE_BACKUP_GCS_KEY" "$1" "$2" <<'PY'
import base64, hashlib, json, os, subprocess, sys, tempfile, time
import urllib.error, urllib.parse, urllib.request

key_path, src, target = sys.argv[1:4]
bucket, _, prefix = target[len("gs://"):].partition("/")
prefix = prefix.strip("/")
name = (prefix + "/" if prefix else "") + os.path.basename(src)
key = json.load(open(key_path))


def b64(raw):
    return base64.urlsafe_b64encode(raw).rstrip(b"=")


now = int(time.time())
unsigned = b64(json.dumps({"alg": "RS256", "typ": "JWT"}).encode()) + b"." + b64(json.dumps({
    "iss": key["client_email"],
    "scope": "https://www.googleapis.com/auth/devstorage.read_write",
    "aud": key["token_uri"], "iat": now, "exp": now + 600}).encode())
# 개인 키는 0600 임시 파일로만 openssl에 넘기고 곧바로 지운다.
with tempfile.NamedTemporaryFile("w") as pem:
    pem.write(key["private_key"])
    pem.flush()
    sig = subprocess.run(["openssl", "dgst", "-sha256", "-sign", pem.name],
                         input=unsigned, capture_output=True, check=True).stdout


def call(request):
    try:
        with urllib.request.urlopen(request, timeout=120) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        sys.exit(f"GCS HTTP {e.code}: {e.read()[:300].decode(errors='replace')}")
    except urllib.error.URLError as e:
        sys.exit(f"GCS 연결 실패: {e.reason}")


token = call(urllib.request.Request(key["token_uri"], data=urllib.parse.urlencode({
    "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
    "assertion": (unsigned + b"." + b64(sig)).decode()}).encode()))["access_token"]

with open(src, "rb") as f:
    data = f.read()
url = (f"https://storage.googleapis.com/upload/storage/v1/b/{urllib.parse.quote(bucket, safe='')}/o"
       f"?uploadType=media&ifGenerationMatch=0&name={urllib.parse.quote(name, safe='')}")
made = call(urllib.request.Request(url, data=data, method="POST", headers={
    "Authorization": f"Bearer {token}", "Content-Type": "application/octet-stream"}))

md5 = base64.b64encode(hashlib.md5(data).digest()).decode()
if int(made.get("size", -1)) != len(data) or made.get("md5Hash") != md5:
    sys.exit(f"올린 사본이 원본과 다릅니다(size {made.get('size')}/{len(data)}, md5 {made.get('md5Hash')}/{md5})")
PY
}

for TARGET in ${REMOTE//,/ }; do
    # 한 곳이 실패해도 다음 대상은 계속 시도한다 — 그래야 사본이 한 부라도 더 남는다.
    case "$TARGET" in
        gs://*)
            if seal && gcs_put "$SEALED" "$TARGET"; then
                echo "원격 사본(암호화): ${TARGET%/}/$(basename "$SEALED")"
                continue
            fi ;;
        *)
            if scp -q -o BatchMode=yes -o ConnectTimeout=10 "$ARCHIVE" "${TARGET%/}/"; then
                echo "원격 사본: ${TARGET%/}/$(basename "$ARCHIVE")"
                continue
            fi ;;
    esac
    echo "원격 사본 실패($TARGET) — 로컬 백업은 정상입니다" >&2
    REMOTE_FAILED=1
done

# 5) 보존 기간 지난 것 정리
find "$DEST" -name 'lyrics-*.db.gz' -mtime "+$KEEP_DAYS" -delete

echo "백업 완료: $ARCHIVE ($SONGS곡, $(du -h "$ARCHIVE" | cut -f1))"
exit "$REMOTE_FAILED"
