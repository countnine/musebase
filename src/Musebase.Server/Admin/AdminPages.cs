using static Musebase.Server.AdminHtml;

namespace Musebase.Server;

/// <summary>
/// 관리자 화면 렌더러 — **DB를 모른다.** 이미 조회된 record만 받으므로 HTML 생성 전체가
/// SQLite 없이 테스트된다. 모든 문자열 삽입은 <see cref="AdminHtml.Esc"/>를 통과한다.
/// </summary>
public static class AdminPages
{
    /// <summary>
    /// 로그인 폼(쿠키가 없을 때).
    ///
    /// 비밀번호를 정해 뒀으면 아이디·비밀번호를 먼저 보여 준다 — 기기마다 긴 토큰을 주소창에
    /// 붙여 넣는 것이 이 화면의 가장 큰 불편이었다. 토큰 입력은 <b>비상구로 남겨 둔다</b>
    /// (비밀번호를 잊거나 해시를 잘못 넣어도 들어갈 수 있어야 한다).
    /// </summary>
    public static string Login(string? error = null, bool passwordEnabled = false) => Layout("로그인", $"""
        {(error is null ? "" : $"<p class=\"bad\">{Esc(error)}</p>")}
        {(passwordEnabled ? $"""
        <h2>로그인</h2>
        <form class="inline" method="post" action="/admin/login">
          <input type="text" name="user" autofocus placeholder="아이디" autocomplete="username" style="min-width:9rem">
          <input type="password" name="password" placeholder="비밀번호" autocomplete="current-password" style="min-width:12rem">
          <button type="submit">로그인</button>
        </form>
        <details>
          <summary>토큰으로 들어가기</summary>
          <p class="meta">비밀번호를 잊었을 때 쓰는 비상구입니다 —
          서버의 <code>/etc/musebase/server.env</code>에 있는 <code>MUSEBASE_TOKEN</code> 값입니다.</p>
          {TokenForm}
        </details>
        """ : $"""
        <h2>관리자 토큰</h2>
        <p class="meta">서버의 <code>/etc/musebase/server.env</code>에 있는 토큰을 넣으세요.
        (<code>/admin?token=…</code>로 열어도 됩니다 — 주소창은 자동으로 정리됩니다.)</p>
        <p class="meta">아이디·비밀번호로 들어오려면 <code>MUSEBASE_ADMIN_PASSWORD</code>를 설정하세요.</p>
        {TokenForm}
        """)}
        """);

    private const string TokenForm = """
        <form class="inline" method="post" action="/admin/login">
          <input type="password" name="token" placeholder="토큰" autocomplete="off" style="min-width:20rem">
          <button type="submit">열기</button>
        </form>
        """;

    public static string Dashboard(
        DashboardModel m, DateTimeOffset nowUtc, TimeZoneInfo tz, string? notice = null)
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
                $"번역 {m.Stats.WithTranslation}곡 · DB {Bytes(m.DatabaseSizeBytes)}"),
            Tile("곡의 의미",
                $"{m.Meanings.Ok}곡",
                m.Meanings.Enabled
                    ? $"자료 부족 {m.Meanings.Insufficient} · 자료 없음 {m.Meanings.NoSource}"
                      + $" · 실패 {m.Meanings.Failed} · 남은 {m.Meanings.Pending}"
                    : "엔진 미구성"));

        // 무엇에 근거해 만들어지는지는 화면에서 보여야 한다 — 설정에만 있으면 나중에 아무도 모른다.
        var sourceLine = m.MeaningSources.Count == 0
            ? ""
            : $"<p class=\"meta\">의미 자료: {Esc(string.Join(" · ", m.MeaningSources))}</p>";

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
            m.TopMisses.Select(MissRowHtml(tz)),
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

        var uploads = Table(SongHeaders, m.RecentUploads.Select(SongRowHtml(tz)),
            "아직 올라온 가사가 없습니다.");

        var noTranslation = Table(SongHeaders, m.WithoutTranslation.Select(SongRowHtml(tz)),
            "모든 곡에 번역이 있습니다.");

        var duplicates = Table(SongHeaders, m.DuplicateCandidates.Select(SongRowHtml(tz)),
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

        var backfill = !m.Meanings.Enabled || m.Meanings.Pending == 0
            ? ""
            : $"""
              <form method="post" action="/admin/meanings/backfill" class="inline" data-busy>
                <input type="hidden" name="csrf" value="{Esc(m.Csrf)}">
                <button type="submit">의미 일괄 생성 ({m.Meanings.Pending}곡)</button>
              </form>
              <span class="meta">한 번에 처리할 곡 수는 <code>MUSEBASE_MEANING_BACKFILL_LIMIT</code>로 정합니다.</span>
              """;

        return Layout("대시보드", $"""
            {(notice is null ? "" : $"<p class=\"ok\">{Esc(notice)}</p>")}
            <div class="tiles">{tiles}</div>
            {sourceLine}
            <p class="meta">각 기기의 <b>로컬 캐시에 없는 곡만</b> 서버로 옵니다 —
            같은 곡을 반복 재생해도 조회 수는 늘지 않습니다(로컬 캐시 → 서버 → 제공자 검색 순).</p>
            {backfill}

            <h2>최근 올라온 가사{More("/admin/search")}</h2>{uploads}
            <h2>최근 조회{More("/admin/list?view=lookups")}</h2>{recent}
            <h2>미스 상위 (7일) — 서버에 없어 각 기기가 직접 찾은 곡{More("/admin/list?view=misses")}</h2>{misses}
            <h2>번역 없는 곡 — 일괄 사전번역 대상{More("/admin/list?view=untranslated")}</h2>{noTranslation}
            <h2>기기별 (7일)</h2>{devices}
            <h2>일별 (7일)</h2>{daily}
            <h2>표기 차이로 갈린 곡 후보 (같은 느슨한 키){More("/admin/list?view=duplicates")}</h2>{duplicates}
            <h2>느슨한 키로 맞은 조회 (7일){More("/admin/list?view=cleaned")}</h2>{cleaned}

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

    public static string SearchPage(
        string? query, IReadOnlyList<SongRow> results, TimeZoneInfo tz, string? meaning = null)
    {
        var empty = (string.IsNullOrWhiteSpace(query), meaning) switch
        {
            (true, LyricsStore.MeaningFilterOk) => "의미가 만들어진 곡이 아직 없습니다.",
            (true, LyricsStore.MeaningFilterNone) => "모든 곡에 의미가 있습니다.",
            (true, _) => "저장된 가사가 없습니다.",
            _ => "검색 결과가 없습니다.",
        };
        var table = Table(SongHeaders, results.Select(SongRowHtml(tz)), empty);

        string Option(string value, string label) =>
            $"<option value=\"{Esc(value)}\"{(meaning == value ? " selected" : "")}>{Esc(label)}</option>";

        var filterLabel = meaning switch
        {
            LyricsStore.MeaningFilterOk => " · 의미 있음",
            LyricsStore.MeaningFilterNone => " · 의미 아직 없음",
            _ => "",
        };

        return Layout("가사 검색", $"""
            <form class="inline" method="get" action="/admin/search">
              <input type="text" name="q" value="{Esc(query)}" placeholder="제목 또는 아티스트" autofocus>
              <select name="meaning">
                {Option("", "의미: 전체")}
                {Option(LyricsStore.MeaningFilterOk, "의미: 있음")}
                {Option(LyricsStore.MeaningFilterNone, "의미: 아직 없음")}
              </select>
              <button type="submit">검색</button>
            </form>
            <p class="meta">{(string.IsNullOrWhiteSpace(query) ? "최근 갱신순" : $"\"{Esc(query)}\" 검색")}{filterLabel} · {results.Count}건</p>
            {table}
            """, "search");
    }

    /// <summary>
    /// 대시보드의 한 섹션을 전부 보여 주는 페이지. 섹션마다 라우트를 파지 않고
    /// <c>?view=</c> 하나로 처리한다 — 표를 만드는 방법은 대시보드와 완전히 같다.
    /// </summary>
    public static string ListPage(string heading, string tableHtml, int count, string? note = null) =>
        Layout(heading, $"""
            <h2>{Esc(heading)}</h2>
            <p class="meta">{count}건{(note is null ? "" : $" · {Esc(note)}")}</p>
            {tableHtml}
            <p class="meta"><a href="/admin">← 대시보드</a></p>
            """, "home");

    /// <summary>`/admin/list?view=` 가 받는 값과 화면 제목. 여기 없는 값은 거절한다.</summary>
    public static readonly IReadOnlyDictionary<string, string> ListViews =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lookups"] = "최근 조회",
            ["misses"] = "미스 상위 (7일)",
            ["untranslated"] = "번역 없는 곡",
            ["duplicates"] = "표기 차이로 갈린 곡 후보",
            ["cleaned"] = "느슨한 키로 맞은 조회 (7일)",
        };

    /// <summary>`/admin/list` 의 표 — 뷰마다 열이 달라 여기서 만든다.</summary>
    public static string ListTable(
        string view, DashboardModel m, TimeZoneInfo tz) => view switch
    {
        "lookups" => Table(
            ["시각", "곡", "아티스트", "결과", "기기"],
            m.Recent.Select(r => $"""
                <tr><td class="nowrap">{Esc(AdminTime.ToLocal(r.At, tz))}</td>
                <td>{SongLink(r.Key, r.Title)}</td><td>{Esc(r.Artist)}</td>
                <td class="{ResultClass(r.Result)}">{Esc(ResultText(r.Result))}</td>
                <td>{Esc(r.Device)}</td></tr>
                """),
            "아직 조회가 없습니다."),

        "misses" => Table(
            ["곡", "아티스트", "횟수", "기기 수", "마지막", ""],
            m.TopMisses.Select(MissRowHtml(tz)),
            "미스 없음 — 요청한 곡이 전부 서버에 있었습니다."),

        "untranslated" => Table(SongHeaders, m.WithoutTranslation.Select(SongRowHtml(tz)),
            "모든 곡에 번역이 있습니다."),

        "duplicates" => Table(SongHeaders, m.DuplicateCandidates.Select(SongRowHtml(tz)),
            "표기 차이로 갈린 곡이 없습니다."),

        _ => Table(
            ["시각", "요청한 곡", "요청한 아티스트", "맞은 곡", "기기"],
            m.CleanedMatches.Select(r => $"""
                <tr><td class="nowrap">{Esc(AdminTime.ToLocal(r.At, tz))}</td>
                <td>{Esc(r.Title)}</td><td>{Esc(r.Artist)}</td>
                <td>{SongLink(r.Key, r.Key ?? "")}</td><td>{Esc(r.Device)}</td></tr>
                """),
            "느슨한 매치가 아직 없습니다."),
    };

    public static string SongPage(
        LyricsEntry entry, IReadOnlyList<DisplayLine> lines, IReadOnlyList<string> langs,
        string? selectedLang, bool showTags, string csrf, TimeZoneInfo tz, string? notice = null,
        MeaningEntry? meaning = null, bool meaningEnabled = false,
        IReadOnlyList<(string Id, string Label, bool Checked)>? meaningSources = null)
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
            {MeaningCard(entry, meaning, csrf, meaningEnabled, meaningSources ?? [])}
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

    /// <summary>
    /// 가사 위에 붙는 "이 곡의 의미" 카드. 의미가 없으면 외부 링크와 생성 버튼만 보인다.
    ///
    /// <b>출처 표기는 의무다</b> — Wikipedia 본문은 CC BY-SA고 Genius·Last.fm도 링크 표기를
    /// 요구하므로 요약과 항상 함께 렌더한다.
    /// </summary>
    private static string MeaningCard(
        LyricsEntry entry, MeaningEntry? meaning, string csrf, bool enabled,
        IReadOnlyList<(string Id, string Label, bool Checked)> sources)
    {
        var key = entry.Key ?? "";
        var geniusUrl = MeaningLinks.Genius(entry.Title, entry.Artist, meaning?.GeniusUrl);

        var musixmatchUrl = MeaningLinks.Musixmatch(entry.Title, entry.Artist, meaning?.MusixmatchUrl);
        var links = $"""
            <a href="{Esc(musixmatchUrl)}" target="_blank" rel="noopener noreferrer">Musixmatch</a>
            · <a href="{Esc(geniusUrl)}" target="_blank" rel="noopener noreferrer">Genius</a>
            """;

        // 어떤 자료로 만들지 그 자리에서 고른다 — 한 곡으로 소스를 바꿔 가며 시험해 볼 수 있다.
        var picker = sources.Count == 0 ? "" : $"""
            <span class="srcpick">{string.Concat(sources.Select(s => $"""
              <label><input type="checkbox" name="src" value="{Esc(s.Id)}"{(s.Checked ? " checked" : "")}> {Esc(s.Label)}</label>
              """))}</span>
            """;

        // data-busy: 제출하면 버튼이 잠기고 스피너가 돈다(외부 API를 여러 번 부르므로 수 초 걸린다).
        var button = !enabled
            ? "<span class=\"meta\">의미 엔진이 구성되지 않았습니다.</span>"
            : $"""
              <form method="post" action="/admin/song/meaning" class="inline" data-busy>
                <input type="hidden" name="key" value="{Esc(key)}">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">{(meaning is null ? "의미 가져오기" : "다시 생성")}</button>
                {picker}
              </form>
              """;

        var bodyHtml = meaning?.Status switch
        {
            MeaningEntry.StatusOk => $"<p>{Esc(meaning.Summary)}</p>",
            // 문단은 보여 준다(사람이 판단할 수 있게) — 다만 의미가 아니라는 것을 앞에 밝힌다.
            MeaningEntry.StatusInsufficient =>
                "<p class=\"warn\">자료 부족 — 모은 자료만으로는 곡의 의미를 판단하지 못했습니다.</p>"
                + $"<p class=\"meta\">{Esc(meaning.Summary)}</p>",
            MeaningEntry.StatusNoSource =>
                "<p class=\"meta\">외부 자료를 찾지 못했습니다 — 위 링크에서 직접 확인해 보세요.</p>",
            MeaningEntry.StatusFailed =>
                "<p class=\"meta\">생성에 실패했습니다(키·쿼타·네트워크).</p>",
            _ => "<p class=\"meta\">아직 만들지 않았습니다.</p>",
        };

        var attribution = MeaningMapper.Attribution(meaning?.Sources);
        var credit = attribution.Count == 0
            ? ""
            : "<p class=\"meta\">출처: " + string.Join(" · ", attribution.Select(a =>
                  string.IsNullOrWhiteSpace(a.Url)
                      ? Esc(a.Name)
                      : $"<a href=\"{Esc(a.Url)}\" target=\"_blank\" rel=\"noopener noreferrer\">{Esc(a.Name)}</a>"))
              + (attribution.Any(a => a.Name == "Wikipedia") ? " (CC BY-SA)" : "")
              + $" · {Esc(meaning?.Engine ?? "-")}/{Esc(meaning?.Model ?? "-")}</p>";

        return $"""
            <h2>이 곡의 의미</h2>
            {bodyHtml}
            {credit}
            <p class="meta">{links}</p>
            {button}
            """;
    }

    // ---- 조각 ----

    /// <summary>곡 목록 표의 열 이름 — 표를 만드는 곳이 여럿이라 한 군데서 정한다.</summary>
    private static readonly string[] SongHeaders =
        ["곡", "아티스트", "출처", "줄", "번역", "의미", "올린 기기", "갱신"];

    private static Func<SongRow, string> SongRowHtml(TimeZoneInfo tz) => r => $"""
        <tr><td>{SongLink(r.Key, r.Title)}</td><td>{Esc(r.Artist)}</td><td>{Esc(r.Service ?? "-")}</td>
        <td>{r.LineCount}{(r.HasInlineTimeTags ? " ●" : "")}</td>
        <td>{Esc(r.Langs.Length == 0 ? "-" : string.Join(",", r.Langs))}</td>
        <td class="nowrap">{MeaningCell(r.MeaningStatus)}</td>
        <td>{Esc(r.UpdatedBy ?? "-")}</td>
        <td class="nowrap">{Esc(AdminTime.ToLocal(r.UpdatedAt, tz))}</td></tr>
        """;

    private static string SongLink(string? key, string text) =>
        string.IsNullOrEmpty(key) ? Esc(text) : $"<a href=\"/admin/song?key={Url(key)}\">{Esc(text)}</a>";

    /// <summary>
    /// 미스 행 한 줄. 그때는 없었어도 <b>지금은 서버에 있을 수 있어</b>, 있으면 곡으로 바로 간다
    /// (없으면 예전처럼 검색으로 보낸다).
    /// </summary>
    private static Func<MissRow, string> MissRowHtml(TimeZoneInfo tz) => r =>
    {
        var action = string.IsNullOrEmpty(r.Key)
            ? $"<a href=\"/admin/search?q={Url(r.Title)}\">검색</a>"
            : $"<a href=\"/admin/song?key={Url(r.Key)}\">가사 보기</a>";
        return $"""
            <tr><td>{SongLink(r.Key, r.Title)}</td><td>{Esc(r.Artist)}</td><td>{r.Count}</td><td>{r.Devices}</td>
            <td class="nowrap">{Esc(AdminTime.ToLocal(r.LastAt, tz))}</td>
            <td class="nowrap">{action}</td></tr>
            """;
    };

    /// <summary>대시보드의 각 목록이 보여 주는 행 수 — 나머지는 "전체 보기"로 넘긴다.</summary>
    public const int DashboardRows = 10;

    /// <summary>섹션 제목 옆의 "전체 보기" 링크.</summary>
    private static string More(string href) =>
        $"<span class=\"meta\"> · <a href=\"{href}\">전체 보기 →</a></span>";

    private static string MeaningCell(string? status) => status switch
    {
        MeaningEntry.StatusOk => "<span class=\"ok\">있음</span>",
        MeaningEntry.StatusInsufficient => "<span class=\"warn\">자료 부족</span>",
        MeaningEntry.StatusNoSource => "<span class=\"meta\">자료 없음</span>",
        MeaningEntry.StatusFailed => "<span class=\"bad\">실패</span>",
        _ => "<span class=\"meta\">-</span>",
    };

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
