using static Musebase.Server.AdminHtml;

namespace Musebase.Server;

/// <summary>
/// 관리자 화면 렌더러 — **DB를 모른다.** 이미 조회된 record만 받으므로 HTML 생성 전체가
/// SQLite 없이 테스트된다. 모든 문자열 삽입은 <see cref="AdminHtml.Esc"/>를 통과한다.
/// </summary>
public static class AdminPages
{
    /// <summary>토큰 입력 폼(쿠키가 없을 때).</summary>
    public static string Login(string? error = null) => Layout("로그인", $"""
        <h2>관리자 토큰</h2>
        <p class="meta">서버의 <code>/etc/musebase/server.env</code>에 있는 토큰을 넣으세요.
        (<code>/admin?token=…</code>로 열어도 됩니다 — 주소창은 자동으로 정리됩니다.)</p>
        {(error is null ? "" : $"<p class=\"bad\">{Esc(error)}</p>")}
        <form class="inline" method="post" action="/admin/login">
          <input type="password" name="token" autofocus placeholder="토큰" style="min-width:20rem">
          <button type="submit">열기</button>
        </form>
        """);

    public static string Dashboard(DashboardModel m, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var last = m.Recent.Count > 0 ? m.Recent[0] : null;

        var tiles = string.Concat(
            Tile("마지막 조회",
                AdminTime.Ago(last?.At, nowUtc),
                last is null ? "아직 조회 없음" : $"{last.Device} · {last.Title}"),
            Tile("오늘 조회",
                $"{m.Today.Total}건",
                $"히트 {m.Today.Hits} / 미스 {m.Today.Miss}"),
            Tile("히트율 (7일)",
                $"{m.Week.Percent}%",
                $"{m.Week.Hits}/{m.Week.Total} · 느슨한 매치 {m.Week.Cleaned}"),
            Tile("보관 중인 가사",
                $"{m.Stats.Songs}곡",
                $"번역 {m.Stats.WithTranslation}곡 · DB {Bytes(m.DatabaseSizeBytes)}"));

        var recent = Table(
            ["시각", "곡", "아티스트", "결과", "기기"],
            m.Recent.Select(r => $"""
                <tr><td class="nowrap">{Esc(AdminTime.ToLocal(r.At, tz))}</td>
                <td>{SongLink(r.Key, r.Title)}</td><td>{Esc(r.Artist)}</td>
                <td class="{ResultClass(r.Result)}">{Esc(ResultText(r.Result))}</td>
                <td>{Esc(r.Device)}</td></tr>
                """),
            "아직 조회가 없습니다 — 기기에서 서버 주소를 넣고 로컬 캐시에 없는 곡을 재생해 보세요.");

        var misses = Table(
            ["곡", "아티스트", "횟수", "기기 수", "마지막", ""],
            m.TopMisses.Select(r => $"""
                <tr><td>{Esc(r.Title)}</td><td>{Esc(r.Artist)}</td><td>{r.Count}</td><td>{r.Devices}</td>
                <td class="nowrap">{Esc(AdminTime.ToLocal(r.LastAt, tz))}</td>
                <td><a href="/admin/search?q={Url(r.Title)}">검색</a></td></tr>
                """),
            "미스 없음 — 요청한 곡이 전부 서버에 있었습니다.");

        var devices = Table(
            ["기기", "조회", "히트", "마지막 접속"],
            m.Devices.Select(r => $"""
                <tr><td>{Esc(r.Device)}</td><td>{r.Lookups}</td><td>{r.Hits}</td>
                <td class="nowrap">{Esc(AdminTime.ToLocal(r.LastAt, tz))}</td></tr>
                """));

        var maxDay = m.Daily.Count == 0 ? 1 : Math.Max(1, m.Daily.Max(d => d.Hits + d.Misses));
        var daily = Table(
            ["날짜", "조회", "히트율", ""],
            m.Daily.Select(d =>
            {
                var total = d.Hits + d.Misses;
                var pct = total == 0 ? 0 : (int)Math.Round(d.Hits * 100.0 / total);
                var width = (int)Math.Round(total * 100.0 / maxDay);
                return $"""
                    <tr><td class="nowrap">{Esc(d.Day)}</td><td>{total}</td><td>{pct}%</td>
                    <td><div class="bar"><span style="width:{width}%"></span></div></td></tr>
                    """;
            }));

        var uploads = Table(
            ["곡", "아티스트", "출처", "줄", "번역", "올린 기기", "갱신"],
            m.RecentUploads.Select(SongRowHtml(tz)));

        var noTranslation = Table(
            ["곡", "아티스트", "출처", "줄", "번역", "올린 기기", "갱신"],
            m.WithoutTranslation.Select(SongRowHtml(tz)),
            "모든 곡에 번역이 있습니다.");

        var duplicates = Table(
            ["곡", "아티스트", "출처", "줄", "번역", "올린 기기", "갱신"],
            m.DuplicateCandidates.Select(SongRowHtml(tz)),
            "표기 차이로 갈린 곡이 없습니다 — 키 정규화가 잘 먹고 있습니다.");

        var cleaned = Table(
            ["시각", "요청한 곡", "요청한 아티스트", "맞은 곡", "기기"],
            m.CleanedMatches.Select(r => $"""
                <tr><td class="nowrap">{Esc(AdminTime.ToLocal(r.At, tz))}</td>
                <td>{Esc(r.Title)}</td><td>{Esc(r.Artist)}</td>
                <td>{SongLink(r.Key, r.Key ?? "")}</td><td>{Esc(r.Device)}</td></tr>
                """),
            "느슨한 매치가 아직 없습니다.");

        var diagnostics = string.Join("", m.Diagnostics.Select(d =>
            $"<tr><td class=\"nowrap\">{Esc(d.Name)}</td><td>{Esc(d.Value)}</td></tr>"));

        return Layout("대시보드", $"""
            <div class="tiles">{tiles}</div>
            <p class="meta">각 기기의 <b>로컬 캐시에 없는 곡만</b> 서버로 옵니다 —
            같은 곡을 반복 재생해도 조회 수는 늘지 않습니다(로컬 캐시 → 서버 → 제공자 검색 순).</p>

            <h2>최근 조회</h2>{recent}
            <h2>미스 상위 (7일) — 서버에 없어 각 기기가 직접 찾은 곡</h2>{misses}
            <h2>기기별 (7일)</h2>{devices}
            <h2>일별 (7일)</h2>{daily}
            <h2>최근 올라온 가사</h2>{uploads}
            <h2>번역 없는 곡 — 일괄 사전번역 대상</h2>{noTranslation}
            <h2>표기 차이로 갈린 곡 후보 (같은 느슨한 키)</h2>{duplicates}
            <h2>느슨한 키로 맞은 조회 (7일)</h2>{cleaned}

            <details>
              <summary>진단 — 현재 요청 헤더 · 서버 상태</summary>
              <p class="meta">기기 이름이 IP로만 보이면 아래 값을 보고
              <code>MUSEBASE_DEVICES=100.x.y.z=거실PC</code> 형식으로 서버 환경변수에 넣으세요.</p>
              <table><tbody>
                {diagnostics}
                <tr><td class="nowrap">업타임</td><td>{Esc(FormatUptime(m.Health.Uptime))}</td></tr>
                <tr><td class="nowrap">서버 메모리</td><td>{Esc(Bytes(m.Health.WorkingSetBytes))}</td></tr>
                <tr><td class="nowrap">디스크 여유</td><td>{Esc(Bytes(m.Health.DiskFreeBytes))}</td></tr>
                <tr><td class="nowrap">조회 기록 보존</td><td>{m.Health.RetentionDays}일</td></tr>
              </tbody></table>
            </details>
            """, "home");
    }

    public static string SearchPage(string? query, IReadOnlyList<SongRow> results, TimeZoneInfo tz)
    {
        var table = Table(
            ["곡", "아티스트", "출처", "줄", "번역", "올린 기기", "갱신"],
            results.Select(SongRowHtml(tz)),
            string.IsNullOrWhiteSpace(query) ? "저장된 가사가 없습니다." : "검색 결과가 없습니다.");

        return Layout("가사 검색", $"""
            <form class="inline" method="get" action="/admin/search">
              <input type="text" name="q" value="{Esc(query)}" placeholder="제목 또는 아티스트" autofocus>
              <button type="submit">검색</button>
            </form>
            <p class="meta">{(string.IsNullOrWhiteSpace(query) ? "최근 갱신순" : $"\"{Esc(query)}\" 검색")} · {results.Count}건</p>
            {table}
            """, "search");
    }

    public static string SongPage(
        LyricsEntry entry, IReadOnlyList<DisplayLine> lines, IReadOnlyList<string> langs,
        string? selectedLang, bool showTags, string csrf, TimeZoneInfo tz, string? notice = null)
    {
        var key = entry.Key ?? "";
        var langLinks = langs.Count == 0
            ? "<span class=\"meta\">번역 없음</span>"
            : string.Join(" · ", langs.Select(l =>
                l.Equals(selectedLang, StringComparison.OrdinalIgnoreCase)
                    ? $"<b>{Esc(l)}</b>"
                    : $"<a href=\"/admin/song?key={Url(key)}&lang={Url(l)}&tags={(showTags ? 1 : 0)}\">{Esc(l)}</a>"));

        var body = lines.Count == 0
            ? $"<pre>{Esc(entry.Lrc)}</pre>"
            : Table(
                showTags ? ["시간", "원문", "번역"] : ["원문", "번역"],
                lines.Select(l => showTags
                    ? $"<tr><td class=\"nowrap meta\">{Esc(l.TimeTag)}</td><td>{Esc(l.Content)}</td><td>{Esc(l.Translation)}</td></tr>"
                    : $"<tr><td>{Esc(l.Content)}</td><td>{Esc(l.Translation)}</td></tr>"));

        return Layout($"{entry.Title} — {entry.Artist}", $"""
            {(notice is null ? "" : $"<p class=\"ok\">{Esc(notice)}</p>")}
            <h2>{Esc(entry.Title)} — {Esc(entry.Artist)}</h2>
            <p class="meta">
              출처 {Esc(entry.Service ?? "-")} · origin {Esc(entry.Origin)} · rev {entry.Revision}
              · 갱신 {Esc(AdminTime.ToLocal(entry.UpdatedAt, tz, "yyyy-MM-dd HH:mm"))}
              {(entry.LineCount is { } n ? $"· {n}줄" : "")}
              {(entry.HasInlineTimeTags == true ? "· 글자 카라오케" : "")}
              <br>key <code>{Esc(key)}</code>
            </p>
            <p class="meta">번역: {langLinks}
              · <a href="/admin/song?key={Url(key)}&lang={Url(selectedLang)}&tags={(showTags ? 0 : 1)}">
                  타임태그 {(showTags ? "숨기기" : "보기")}</a>
              · <a href="/admin/raw?key={Url(key)}">원문(.lrc)</a></p>
            <p class="meta">곡의 배경·의미:
              <a href="{Esc(MeaningLinks.MusixmatchSearch(entry.Title, entry.Artist))}"
                 target="_blank" rel="noopener noreferrer">Musixmatch</a>
              · <a href="{Esc(MeaningLinks.GeniusSearch(entry.Title, entry.Artist))}"
                 target="_blank" rel="noopener noreferrer">Genius</a></p>
            {body}

            <h2>편집</h2>
            <p class="meta">저장하면 <code>origin=user</code>로 기록되어 각 기기의 자동 검색 결과가
            이 가사를 덮어쓰지 못합니다. 형식은 확장 LRC 그대로 유지하세요.</p>
            <form method="post" action="/admin/song/edit">
              <input type="hidden" name="key" value="{Esc(key)}">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <textarea name="lrc" spellcheck="false">{Esc(entry.Lrc)}</textarea>
              <div class="inline" style="margin-top:.5rem"><button type="submit">저장</button></div>
            </form>

            <form method="post" action="/admin/song/delete" style="margin-top:1rem"
                  onsubmit="return true">
              <input type="hidden" name="key" value="{Esc(key)}">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <input type="hidden" name="confirm" value="1">
              <button class="danger" type="submit">이 곡 삭제</button>
              <span class="meta">삭제하면 다음에 어느 기기든 재생할 때 다시 검색해 새로 채웁니다.</span>
            </form>
            """, "search");
    }

    // ---- 조각 ----

    private static Func<SongRow, string> SongRowHtml(TimeZoneInfo tz) => r => $"""
        <tr><td>{SongLink(r.Key, r.Title)}</td><td>{Esc(r.Artist)}</td><td>{Esc(r.Service ?? "-")}</td>
        <td>{r.LineCount}{(r.HasInlineTimeTags ? " ●" : "")}</td>
        <td>{Esc(r.Langs.Length == 0 ? "-" : string.Join(",", r.Langs))}</td>
        <td>{Esc(r.UpdatedBy ?? "-")}</td>
        <td class="nowrap">{Esc(AdminTime.ToLocal(r.UpdatedAt, tz))}</td></tr>
        """;

    private static string SongLink(string? key, string text) =>
        string.IsNullOrEmpty(key) ? Esc(text) : $"<a href=\"/admin/song?key={Url(key)}\">{Esc(text)}</a>";

    private static string ResultText(string result) => result switch
    {
        LyricsEntry.MatchExact => "히트",
        LyricsEntry.MatchCleaned => "히트(느슨)",
        _ => "미스",
    };

    private static string ResultClass(string result) => result switch
    {
        LyricsEntry.MatchExact => "ok",
        LyricsEntry.MatchCleaned => "warn",
        _ => "bad",
    };

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}일 {t.Hours}시간" : $"{(int)t.TotalHours}시간 {t.Minutes}분";
}
