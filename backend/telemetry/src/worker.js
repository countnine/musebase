// Musebase 텔레메트리 수집 Worker.
// - POST /ingest : 앱이 보내는 익명 이벤트 배치 저장 (옵트인 사용자만 전송)
// - GET  /stats  : 최근 30일 이벤트 종류별 건수 (투명성 차원에서 공개)
// - GET  /healthz
// 개인정보 없음: client_id는 앱이 만든 랜덤 GUID. IP는 저장하지 않는다.

const MAX_BODY_BYTES = 64 * 1024; // 배치 상한 64KB
const MAX_EVENTS_PER_BATCH = 100;
const PLATFORMS = new Set(["windows", "android", "browser", "macos", "ios"]);
// 앱이 정의한 이벤트만 수용(오남용·쓰레기 데이터 차단). contracts/telemetry-events.md와 동기화.
const EVENT_TYPES = new Set([
  "app_session",
  "playback_source",
  "lyrics_search",
  "lyrics_not_found",
  "wrong_lyrics",
  "translation",
  "feature_use",
  "error",
]);

function json(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" },
  });
}

async function handleIngest(request, env) {
  if (request.headers.get("content-type")?.includes("application/json") !== true)
    return json({ error: "content-type must be application/json" }, 415);

  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) return json({ error: "body too large" }, 413);

  let body;
  try { body = JSON.parse(raw); } catch { return json({ error: "invalid json" }, 400); }

  const { clientId, platform, appVersion, events } = body ?? {};
  if (typeof clientId !== "string" || clientId.length < 8 || clientId.length > 64)
    return json({ error: "clientId" }, 400);
  if (!PLATFORMS.has(platform)) return json({ error: "platform" }, 400);
  if (typeof appVersion !== "string" || appVersion.length > 32)
    return json({ error: "appVersion" }, 400);
  if (!Array.isArray(events) || events.length === 0 || events.length > MAX_EVENTS_PER_BATCH)
    return json({ error: "events" }, 400);

  const now = new Date().toISOString();
  const stmt = env.DB.prepare(
    "INSERT INTO events (received_at, client_id, platform, app_version, type, props) VALUES (?1, ?2, ?3, ?4, ?5, ?6)"
  );
  const rows = [];
  for (const e of events) {
    if (!e || !EVENT_TYPES.has(e.type)) return json({ error: `unknown event type` }, 400);
    const props = JSON.stringify(e.props ?? {});
    if (props.length > 4096) return json({ error: "props too large" }, 400);
    rows.push(stmt.bind(now, clientId, platform, appVersion, e.type, props));
  }
  await env.DB.batch(rows);
  return json({ ok: true, stored: rows.length });
}

// ---- 관리자 조회 페이지 (토큰 보호) -------------------------------------------
// GET /admin?token=<ADMIN_TOKEN>  — 품질 리포트(틀린가사/검색실패, 빈도순) + 현황 요약 HTML.
// 토큰은 Worker Secret(ADMIN_TOKEN)으로 관리: `wrangler secret put ADMIN_TOKEN`

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, c =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function handleAdmin(url, env) {
  const token = url.searchParams.get("token") ?? "";
  if (!env.ADMIN_TOKEN || token !== env.ADMIN_TOKEN)
    return new Response("unauthorized", { status: 401 });

  const [wrong, notFound, summary, versions] = await Promise.all([
    env.DB.prepare(
      `SELECT json_extract(props,'$.title') AS title, json_extract(props,'$.artist') AS artist,
              json_extract(props,'$.source') AS source, COUNT(*) AS cnt,
              COUNT(DISTINCT client_id) AS clients, MAX(received_at) AS last_seen
         FROM events WHERE type='wrong_lyrics'
        GROUP BY title, artist, source ORDER BY cnt DESC, last_seen DESC LIMIT 200`).all(),
    env.DB.prepare(
      `SELECT json_extract(props,'$.title') AS title, json_extract(props,'$.artist') AS artist,
              COUNT(*) AS cnt, COUNT(DISTINCT client_id) AS clients, MAX(received_at) AS last_seen
         FROM events WHERE type='lyrics_not_found'
        GROUP BY title, artist ORDER BY cnt DESC, last_seen DESC LIMIT 200`).all(),
    env.DB.prepare(
      `SELECT type, COUNT(*) AS cnt, COUNT(DISTINCT client_id) AS clients, MAX(received_at) AS last_seen
         FROM events GROUP BY type ORDER BY cnt DESC`).all(),
    env.DB.prepare(
      `SELECT platform, app_version, COUNT(DISTINCT client_id) AS clients, MAX(received_at) AS last_seen
         FROM events GROUP BY platform, app_version ORDER BY last_seen DESC LIMIT 50`).all(),
  ]);

  const rows = (rs, cols) => rs.results.length
    ? rs.results.map(r => `<tr>${cols.map(c => `<td>${esc(r[c])}</td>`).join("")}</tr>`).join("")
    : `<tr><td colspan="${cols.length}" class="empty">아직 데이터 없음</td></tr>`;

  const html = `<!doctype html><html lang="ko"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><meta name="robots" content="noindex">
<title>Musebase 텔레메트리 관리자</title><style>
  body{font-family:system-ui,sans-serif;margin:2rem auto;max-width:64rem;padding:0 1rem;background:#111;color:#eee}
  h1{font-size:1.3rem} h2{font-size:1.05rem;margin-top:2rem;border-bottom:1px solid #333;padding-bottom:.3rem}
  table{border-collapse:collapse;width:100%;font-size:.85rem} th,td{border:1px solid #333;padding:.35rem .5rem;text-align:left}
  th{background:#1c1c1c} tr:nth-child(even){background:#181818} .empty{color:#777;text-align:center}
  .meta{color:#888;font-size:.8rem}
</style></head><body>
<h1>Musebase 텔레메트리 관리자</h1>
<p class="meta">생성 시각(UTC): ${new Date().toISOString()} · 원본 보존 90일 · <a href="/stats" style="color:#7cc4ff">공개 집계</a></p>
<h2>틀린 가사 리포트 (빈도순, 상위 200)</h2>
<table><tr><th>곡명</th><th>아티스트</th><th>가사 소스</th><th>건수</th><th>사용자수</th><th>마지막</th></tr>
${rows(wrong, ["title", "artist", "source", "cnt", "clients", "last_seen"])}</table>
<h2>가사 검색 실패 곡 (빈도순, 상위 200)</h2>
<table><tr><th>곡명</th><th>아티스트</th><th>건수</th><th>사용자수</th><th>마지막</th></tr>
${rows(notFound, ["title", "artist", "cnt", "clients", "last_seen"])}</table>
<h2>이벤트 전체 현황</h2>
<table><tr><th>종류</th><th>건수</th><th>고유 사용자</th><th>마지막</th></tr>
${rows(summary, ["type", "cnt", "clients", "last_seen"])}</table>
<h2>플랫폼·버전 분포</h2>
<table><tr><th>플랫폼</th><th>앱 버전</th><th>고유 사용자</th><th>마지막</th></tr>
${rows(versions, ["platform", "app_version", "clients", "last_seen"])}</table>
</body></html>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8" } });
}

async function handleStats(env) {
  const since = new Date(Date.now() - 30 * 24 * 3600 * 1000).toISOString();
  const { results } = await env.DB.prepare(
    `SELECT type, COUNT(*) AS count, COUNT(DISTINCT client_id) AS clients
       FROM events WHERE received_at >= ?1 GROUP BY type ORDER BY count DESC`
  ).bind(since).all();
  return json({ since, totals: results });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/healthz") return new Response("ok");
    if (url.pathname === "/ingest" && request.method === "POST") return handleIngest(request, env);
    if (url.pathname === "/stats" && request.method === "GET") return handleStats(env);
    if (url.pathname === "/admin" && request.method === "GET") return handleAdmin(url, env);
    return json({ error: "not found" }, 404);
  },
};
