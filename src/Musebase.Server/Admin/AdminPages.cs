using Musebase.Core.Translation;
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
        <form class="inline" method="post" action="{Routes.Base}/login">
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
        (<code>{Routes.Base}?token=…</code>로 열어도 됩니다 — 주소창은 자동으로 정리됩니다.)</p>
        <p class="meta">아이디·비밀번호로 들어오려면 <code>MUSEBASE_ADMIN_PASSWORD</code>를 설정하세요.</p>
        {TokenForm}
        """)}
        """);

    private static readonly string TokenForm = $"""
        <form class="inline" method="post" action="{Routes.Base}/login">
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

        var ads = AdTable(m.AdTitles ?? [], m.Csrf, tz);

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
              <p><a class="chip on" href="{Routes.Base}/jobs/new?kind=meaning&scope=all&skip=1">
              의미 일괄 생성 ({m.Meanings.Pending}곡) →</a></p>
              <span class="meta">다음 화면에서 <b>대상 범위</b>(좋아요한 곡·조회 미스 상위 등)와 곡 수를 고릅니다{(m.MeaningEngine is { Engine: not "none" } e ? $" · 모델 {e.Engine} / {Esc(e.Model)}" : "")}.
              생성은 뒤에서 돌고 진행 상황은 <a href="{Routes.Base}/jobs">일괄 작업</a> 화면에서 봅니다(중간에 멈출 수 있습니다).</span>
              """;

        // 번역 엔진이 켜져 있을 때만 진입점을 보여 준다 — 누를 수 없는 링크를 두면 사람이
        // 눌러 보고 거절 알림을 받는다.
        var translateJob = m.TranslationEngine is { Enabled: true } t
            ? $"""
              <p><a class="chip on" href="{Routes.Base}/jobs/new?kind=translate&scope=loved-any&skip=1&lang={Url(t.Lang)}">
              가사 일괄 번역 ({Esc(t.Lang)}) →</a></p>
              """
            : "";

        var lastfm = LastFmCard(m.LastFm, m.Csrf) + SpotifyCard(m.Spotify, m.Csrf);
        var engine = MeaningEngineCardHtml(m.MeaningEngine, m.Csrf)
                   + TranslationEngineCardHtml(m.TranslationEngine, m.Csrf);

        return Layout("대시보드", $"""
            {(notice is null ? "" : $"<p class=\"ok\">{Esc(notice)}</p>")}
            <div class="tiles">{tiles}</div>
            {sourceLine}
            <p class="meta">각 기기의 <b>로컬 캐시에 없는 곡만</b> 서버로 옵니다 —
            같은 곡을 반복 재생해도 조회 수는 늘지 않습니다(로컬 캐시 → 서버 → 제공자 검색 순).</p>
            {engine}
            {backfill}
            {translateJob}
            {lastfm}

            <h2>최근 올라온 가사{More(Routes.Base + "/search")}</h2>{uploads}
            <h2>최근 조회{More(Routes.Base + "/list?view=lookups")}</h2>{recent}
            <h2>미스 상위 (7일) — 서버에 없어 각 기기가 직접 찾은 곡{More(Routes.Base + "/list?view=misses")}</h2>{misses}
            <h2>번역 없는 곡 — 일괄 사전번역 대상{More(Routes.Base + "/list?view=untranslated")}</h2>{noTranslation}
            <h2>기기별 (7일)</h2>{devices}
            <h2>일별 (7일)</h2>{daily}
            <h2>광고로 차단한 제목{More(Routes.Base + "/list?view=ads")}</h2>{ads}
            <h2>표기 차이로 갈린 곡 후보 (같은 느슨한 키){More(Routes.Base + "/list?view=duplicates")}</h2>{duplicates}
            <h2>느슨한 키로 맞은 조회 (7일){More(Routes.Base + "/list?view=cleaned")}</h2>{cleaned}

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

    /// <summary>
    /// 외부 계정 승인(Last.fm·Spotify)에서 돌아왔을 때 보여 주는 <b>중간 한 칸</b>.
    ///
    /// <b>왜 곧바로 대시보드로 보내지 않는가.</b> 관리자 쿠키는 <c>SameSite=Strict</c>라
    /// 외부 사이트에서 시작된 이동에는 실리지 않는다 — 콜백에서 쿠키를 다시 구워 줘도
    /// <b>그 직후의 리다이렉트까지가 같은 크로스사이트 이동</b>이라 쿠키가 빠진 채 도착하고,
    /// 결과 화면 아래에 로그인 폼이 붙어 "로그인했는데 또 로그인하라"는 화면이 됐다.
    /// 우리 페이지를 한 번 거치면 그다음 이동은 same-site라 쿠키가 정상으로 실린다.
    ///
    /// 그래서 이 화면은 <b>결과만 보여 주고 곧바로 대시보드로 넘긴다</b>(meta refresh —
    /// 스크립트를 쓰면 CSP 해시가 바뀐다). 넘어간 대시보드에도 같은 문구가 알림으로 다시 뜬다.
    /// </summary>
    public static string ExternalResult(string message, bool ok)
    {
        var target = $"{Routes.Base}?notice={Url(message)}";

        return Layout(ok ? "연결됨" : "연결 실패", $"""
            <h2>{(ok ? "연결됐습니다" : "연결하지 못했습니다")}</h2>
            <p class="{(ok ? "ok" : "bad")}">{Esc(message)}</p>
            <p class="meta">잠시 후 대시보드로 돌아갑니다 —
            바로 가려면 <a href="{Esc(target)}">여기</a>를 누르세요.</p>
            """, null, $"""<meta http-equiv="refresh" content="2;url={Esc(target)}">""");
    }

    /// <summary>
    /// 일괄 작업 진행 화면.
    ///
    /// <b>새로고침은 실행 중일 때만 단다</b> — 끝나면 <c>&lt;meta http-equiv="refresh"&gt;</c>가
    /// 사라져 폴링이 저절로 멎는다. 스크립트로 폴링하지 않는 이유는 CSP다(관리 화면은 스크립트
    /// 하나의 해시만 허용한다 — <see cref="AdminHtml.ScriptCsp"/>).
    ///
    /// 이 화면은 <b>DB를 건드리지 않는다</b>. 2초마다 도는 자리라 여기서 조회를 하면
    /// 잡이 앱의 가사 조회를 막는 원인이 정확히 여기가 된다.
    /// </summary>
    public static string JobPage(BulkJobState? state, string csrf, TimeZoneInfo tz, string? notice = null)
    {
        var head = notice is null ? "" : $"<p class=\"ok\">{Esc(notice)}</p>";

        if (state is null)
            return Layout("일괄 작업", $"""
                {head}
                <h2>일괄 작업</h2>
                <p class="meta">이 서버가 켜진 뒤로 실행한 작업이 없습니다.
                <a href="{Routes.Base}">대시보드</a>에서 시작하세요.</p>
                """, "jobs");

        var tiles = string.Concat(
            Tile("진행", $"{state.Done} / {state.Total}곡", $"{state.Percent}% · {FormatElapsed(state.Elapsed)}"),
            Tile("성공", $"{state.Ok}곡", $"건너뜀 {state.Skipped} · 자료 없음 {state.NoSource} · 실패 {state.Failed}"),
            Tile($"쓴 {state.UnitName}", $"{state.Units:N0}{state.UnitName}",
                state.Budget > 0 ? $"상한 {state.Budget:N0}{state.UnitName}" : "상한 없음"),
            Tile("시작", AdminTime.ToLocal(state.StartedAt.ToString("o"), tz),
                state.EndedAt is null ? "실행 중" : $"끝 {AdminTime.ToLocal(state.EndedAt.Value.ToString("o"), tz)}"));

        // 진행 중에는 "지금 무엇을" 이 가장 궁금하고, 끝난 뒤에는 "왜 멈췄나"가 가장 궁금하다.
        var line = state.IsRunning
            ? $"<p class=\"meta\">지금: {Esc(state.CurrentLabel ?? "준비 중")}</p>"
            : $"<p class=\"{StatusClass(state.Status)}\">{Esc(StatusText(state.Status))}"
              + $"{(state.Detail is null ? "" : " — " + Esc(state.Detail))}</p>";

        var cancel = !state.IsRunning ? "" : $"""
            <form method="post" action="{Routes.Base}/jobs/cancel" class="inline" data-busy
                  data-confirm="지금 처리 중인 곡이 끝나면 멈춥니다. 중지할까요?">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <button type="submit" class="danger">중지</button>
            </form>
            <p class="meta">중지는 <b>곡 경계</b>에서 먹습니다 — 처리 중인 곡을 반쯤 저장하지 않기 위해서입니다.
            이미 끝난 곡은 그대로 남고, 남은 곡은 손대지 않습니다.</p>
            """;

        return Layout($"일괄 작업 — {StatusText(state.Status)}", $"""
            {head}
            <h2>{Esc(state.Label)}</h2>
            <div class="bar" style="margin:.5rem 0 1rem"><span style="width:{state.Percent}%"></span></div>
            <div class="tiles">{tiles}</div>
            {line}
            {cancel}
            <p class="meta">서버를 재시작하면 이 기록은 사라집니다 — 이미 저장된 결과는 남습니다
            (잡은 곡 단위로 저장합니다). 요약 한 줄은 <code>journalctl -u musebase-server</code>에도 남습니다.</p>
            """, "jobs", state.IsRunning ? """<meta http-equiv="refresh" content="2">""" : null);
    }

    /// <summary>
    /// 일괄 작업 실행 확인 화면. <b>GET이다</b> — 아무것도 바꾸지 않으므로 새로고침해도 안전하고,
    /// 범위를 바꿔 보는 것이 그냥 링크를 누르는 일이 된다(POST였다면 매번 CSRF·PRG가 붙는다).
    ///
    /// 여기서 보여 주는 것은 <b>실행 전에 알아야 하는 것</b>뿐이다: 대상 곡 수, 추정 사용량,
    /// 예상 비용, 그리고 이번 실행에서 넘지 않을 상한. 상한은 곡 <b>경계</b>에서만 걸린다.
    /// </summary>
    public static string JobConfirmPage(JobPlan plan, string csrf, string? notice = null)
    {
        var kindName = plan.Kind == BulkJobKind.Meaning ? "의미 일괄 생성" : "가사 일괄 번역";

        var scopes = string.Concat(BulkScope.Known.Select(s =>
        {
            var on = s == plan.Scope ? " on" : "";
            return $"<a class=\"chip{on}\" href=\"{Routes.Base}/jobs/new?{plan.Query(scope: s)}\">"
                 + $"{Esc(BulkScope.Label(s))}</a>";
        }));

        var skipLabel = plan.Kind == BulkJobKind.Meaning
            ? "의미를 한 번도 만들어 보지 않은 곡만"
            : $"{Esc(plan.Lang ?? "")} 번역이 없는 곡만";
        var skip = $"""
            <div class="chips">
              <a class="chip{(plan.SkipExisting ? " on" : "")}"
                 href="{Routes.Base}/jobs/new?{plan.Query(skip: true)}">{skipLabel}</a>
              <a class="chip{(plan.SkipExisting ? "" : " on")}"
                 href="{Routes.Base}/jobs/new?{plan.Query(skip: false)}">범위의 곡 전부(다시 만들기)</a>
            </div>
            """;

        var estimate = string.Concat(
            Tile("대상", $"{plan.Targets}곡", plan.Targets > plan.Limit ? $"이번에는 앞의 {plan.Limit}곡" : "전부 이번에"),
            Tile($"추정 {plan.UnitName}", $"{plan.Units:N0}{plan.UnitName}",
                plan.Kind == BulkJobKind.Translation ? "캐시에 이미 있는 줄은 뺐습니다" : "곡당 1회 호출"),
            Tile("예상 비용", plan.Cost ?? "환산 불가", plan.Cost is null ? "무료 한도 또는 토큰 과금" : "요청 기준 추정"),
            Tile("엔진", plan.Engine, plan.Enabled ? "구성됨" : "구성되지 않음"));

        var run = !plan.Enabled
            ? $"<p class=\"bad\">엔진이 구성되지 않았습니다 — <a href=\"{Routes.Base}\">대시보드</a>에서 먼저 설정하세요.</p>"
            : plan.Targets == 0
            ? "<p class=\"meta\">이 범위에 처리할 곡이 없습니다 — 위에서 범위를 바꿔 보세요.</p>"
            : $"""
              <form method="post" action="{Routes.Base}/jobs/start" class="inline" data-busy
                    data-confirm="{Esc($"{Math.Min(plan.Targets, plan.Limit)}곡 · 약 {plan.Units:N0}{plan.UnitName}를 처리합니다. 시작할까요?")}">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <input type="hidden" name="kind" value="{(plan.Kind == BulkJobKind.Meaning ? "meaning" : "translate")}">
                <input type="hidden" name="scope" value="{Esc(plan.Scope)}">
                <input type="hidden" name="skip" value="{(plan.SkipExisting ? "1" : "0")}">
                <input type="hidden" name="lang" value="{Esc(plan.Lang ?? "")}">
                {(notice is null ? "" : "<input type=\"hidden\" name=\"confirm\" value=\"1\">")}
                <label class="meta">곡 수 <input type="number" name="limit" value="{plan.Limit}" min="1" max="5000"></label>
                <label class="meta">{Esc(plan.UnitName)} 상한 <input type="number" name="budget" value="{plan.Budget}" min="1"></label>
                <button type="submit">실행</button>
              </form>
              <p class="meta">상한에 닿으면 <b>곡 경계에서</b> 멈추고 남은 곡은 손대지 않습니다 —
              처리 중에 끊으면 반쯤 만들어진 결과가 저장됩니다.</p>
              """;

        return Layout($"{kindName} — 실행 확인", $"""
            <h2>{Esc(kindName)}</h2>
            {(notice is null ? "" : $"<p class=\"warn\">{Esc(notice)}</p>")}
            <p class="meta">대상 범위</p>
            <div class="chips">{scopes}</div>
            <p class="meta">건너뛰기</p>
            {skip}
            <div class="tiles">{estimate}</div>
            {(plan.Warning is null ? "" : $"<p class=\"warn\">{Esc(plan.Warning)}</p>")}
            {run}
            <p class="meta">진행 상황은 <a href="{Routes.Base}/jobs">일괄 작업</a> 화면에서 봅니다.
            한 번에 하나만 돌 수 있습니다.</p>
            """, "jobs");
    }

    private static string StatusText(BulkJobStatus status) => status switch
    {
        BulkJobStatus.Running => "실행 중",
        BulkJobStatus.Done => "완료",
        BulkJobStatus.Cancelled => "중지됨",
        _ => "멈춤",
    };

    private static string StatusClass(BulkJobStatus status) => status switch
    {
        BulkJobStatus.Done => "ok",
        BulkJobStatus.Stopped => "bad",
        _ => "warn",
    };

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{(int)t.TotalSeconds}초";

    /// <summary>한 페이지에 보여 주는 곡 수. 30건이면 화면 하나에 들어와 스크롤 없이 훑을 수 있다.</summary>
    public const int PageSize = 30;

    /// <summary>
    /// 가사 검색·목록. 필터는 <b>칩</b>으로 두고 각 칩에 건수를 박는다 — 드롭다운은 열어 보기
    /// 전까지 무엇이 있는지, 각각 몇 곡인지 알 수 없었다.
    ///
    /// 칩은 서로 겹치지 않는다(<see cref="LyricsStore.FilterLoved"/>만 다른 축). 건수가 0인 칩은
    /// 아예 그리지 않는다 — 누를 이유가 없는 칸이 줄지어 있으면 있는 것들이 묻힌다.
    /// </summary>
    public static string SearchPage(
        string? query, IReadOnlyList<SongRow> results, TimeZoneInfo tz, string? meaning = null,
        SongCounts? counts = null, int page = 1, int total = -1)
    {
        var empty = (string.IsNullOrWhiteSpace(query), meaning) switch
        {
            (true, LyricsStore.MeaningFilterOk) => "의미가 만들어진 곡이 아직 없습니다.",
            (true, LyricsStore.MeaningFilterNone) => "모든 곡에 의미가 있습니다.",
            (true, LyricsStore.MeaningFilterPending) => "모든 곡을 한 번씩은 만들어 봤습니다.",
            (true, LyricsStore.MeaningFilterInsufficient) => "자료가 부족했던 곡이 없습니다.",
            (true, LyricsStore.MeaningFilterNoSource) => "자료를 못 찾은 곡이 없습니다.",
            (true, LyricsStore.FilterLoved) =>
                "좋아요한 곡이 없습니다 — 대시보드에서 [Last.fm 좋아요 동기화]를 먼저 눌러 주세요.",
            (true, _) => "저장된 가사가 없습니다.",
            _ => "검색 결과가 없습니다.",
        };
        var table = Table(SongHeaders, results.Select(SongRowHtml(tz)), empty);

        var filterLabel = meaning switch
        {
            LyricsStore.MeaningFilterOk => " · 의미 있음",
            LyricsStore.MeaningFilterNone => " · 의미 아직 없음",
            LyricsStore.MeaningFilterPending => " · 아직 안 만듦",
            LyricsStore.MeaningFilterInsufficient => " · 자료 부족",
            LyricsStore.MeaningFilterNoSource => " · 자료 없음",
            LyricsStore.MeaningFilterFailed => " · 생성 실패",
            LyricsStore.FilterLoved => " · Last.fm 좋아요",
            _ => "",
        };

        return Layout("가사 검색", $"""
            <form class="inline" method="get" action="{Routes.Base}/search">
              <input type="text" name="q" value="{Esc(query)}" placeholder="제목 또는 아티스트" autofocus>
              <input type="hidden" name="meaning" value="{Esc(meaning)}">
              <button type="submit">검색</button>
            </form>
            {Chips(query, meaning, counts)}
            <p class="meta">{(string.IsNullOrWhiteSpace(query) ? "최근 갱신순" : $"\"{Esc(query)}\" 검색")}{filterLabel}{Range(page, results.Count, total)}</p>
            {table}
            {Pager(query, meaning, page, total)}
            """, "search");
    }

    /// <summary>필터 칩 한 줄. 건수를 모르면(옛 호출부) 칩 자체를 그리지 않는다.</summary>
    private static string Chips(string? query, string? current, SongCounts? counts)
    {
        if (counts is null) return "";

        (string Value, string Label)[] all =
        [
            ("", "전체"),
            (LyricsStore.MeaningFilterOk, "의미 있음"),
            (LyricsStore.MeaningFilterPending, "아직 안 만듦"),
            (LyricsStore.MeaningFilterInsufficient, "자료 부족"),
            (LyricsStore.MeaningFilterNoSource, "자료 없음"),
            (LyricsStore.MeaningFilterFailed, "생성 실패"),
            (LyricsStore.FilterLoved, "♥ 즐겨찾기"),
            (LyricsStore.FilterSpotify, "Spotify 좋아요"),
        ];

        var chips = all
            // 전체와 지금 고른 칩은 0건이어도 남긴다 — 사라지면 되돌아올 길이 없다.
            .Where(c => counts.For(c.Value) > 0 || c.Value.Length == 0 || c.Value == (current ?? ""))
            .Select(c =>
            {
                var on = (current ?? "") == c.Value ? " on" : "";
                var href = $"{Routes.Base}/search?q={Url(query ?? "")}&meaning={Url(c.Value)}";
                return $"<a class=\"chip{on}\" href=\"{href}\">{Esc(c.Label)} <b>{counts.For(c.Value)}</b></a>";
            });

        return $"<div class=\"chips\">{string.Concat(chips)}</div>";
    }

    /// <summary>"31–60 · 전체 1123건" — 지금 몇 번째를 보고 있는지.</summary>
    private static string Range(int page, int shown, int total)
    {
        if (total < 0) return $" · {shown}건";
        if (shown == 0) return $" · 전체 {total}건";
        var first = ((page - 1) * PageSize) + 1;
        return $" · {first}–{first + shown - 1} · 전체 {total}건";
    }

    /// <summary>
    /// 이전·다음. 예전에는 200건에서 말없이 잘려, 201번째 곡부터는 검색어를 정확히 아는 사람만
    /// 볼 수 있었다(잘렸다는 안내조차 없었다).
    /// </summary>
    private static string Pager(string? query, string? meaning, int page, int total)
    {
        if (total < 0) return "";
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        if (pages <= 1) return "";

        string Link(int target, string label) =>
            target < 1 || target > pages
                ? $"<span class=\"dim\">{label}</span>"
                : $"<a href=\"{Routes.Base}/search?q={Url(query ?? "")}&meaning={Url(meaning ?? "")}&page={target}\">{label}</a>";

        return $"""
            <p class="meta pager">{Link(page - 1, "‹ 이전")} · {page} / {pages} · {Link(page + 1, "다음 ›")}</p>
            """;
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
            <p class="meta"><a href="{Routes.Base}">← 대시보드</a></p>
            """, "home");

    /// <summary>`/musebase/list?view=` 가 받는 값과 화면 제목. 여기 없는 값은 거절한다.</summary>
    public static readonly IReadOnlyDictionary<string, string> ListViews =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lookups"] = "최근 조회",
            ["misses"] = "미스 상위 (7일)",
            ["untranslated"] = "번역 없는 곡",
            ["duplicates"] = "표기 차이로 갈린 곡 후보",
            ["ads"] = "광고로 차단한 제목",
            ["cleaned"] = "느슨한 키로 맞은 조회 (7일)",
        };

    /// <summary>`/musebase/list` 의 표 — 뷰마다 열이 달라 여기서 만든다.</summary>
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

        "ads" => AdTable(m.AdTitles ?? [], m.Csrf, tz),

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
        IReadOnlyList<(string Id, string Label, bool Checked)>? meaningSources = null,
        SongLinks? links = null, LoveState? love = null, SpotifyState? spotify = null)
    {
        var key = entry.Key ?? "";
        var langLinks = langs.Count == 0
            ? "<span class=\"meta\">번역 없음</span>"
            : string.Join(" · ", langs.Select(l =>
                l.Equals(selectedLang, StringComparison.OrdinalIgnoreCase)
                    ? $"<b>{Esc(l)}</b>"
                    : $"<a href=\"{Routes.Base}/song?key={Url(key)}&lang={Url(l)}&tags={(showTags ? 1 : 0)}\">{Esc(l)}</a>"));

        var body = lines.Count == 0
            ? $"<pre>{Esc(entry.Lrc)}</pre>"
            : Table(
                showTags ? ["시간", "원문", "번역"] : ["원문", "번역"],
                lines.Select(l => showTags
                    ? $"<tr><td class=\"nowrap meta\">{Esc(l.TimeTag)}</td><td>{Esc(l.Content)}</td><td>{Esc(l.Translation)}</td></tr>"
                    : $"<tr><td>{Esc(l.Content)}</td><td>{Esc(l.Translation)}</td></tr>"));

        // 커버가 없으면 자리를 아예 그리지 않는다 — 깨진 이미지 아이콘이 더 나쁘다.
        // 크기는 CSS가 정한다 — 오른쪽 정보 단의 높이에 맞춰 늘어난다(정사각 유지).
        var cover = string.IsNullOrWhiteSpace(links?.CoverUrl)
            ? ""
            : $"""<img class="cover" src="{Esc(links!.CoverUrl)}" alt="{Esc(entry.Title)} 커버">""";

        return Layout($"{entry.Title} — {entry.Artist}", $"""
            {(notice is null ? "" : $"<p class=\"ok\">{Esc(notice)}</p>")}
            <div class="song">
            {cover}
            <div>
            <h2>{Esc(entry.Title)} — {Esc(entry.Artist)}</h2>
            <p class="meta">
              출처 {Esc(entry.Service ?? "-")} · origin {Esc(entry.Origin)} · rev {entry.Revision}
              · 갱신 {Esc(AdminTime.ToLocal(entry.UpdatedAt, tz, "yyyy-MM-dd HH:mm"))}
              {(entry.LineCount is { } n ? $"· {n}줄" : "")}
              {(entry.HasInlineTimeTags == true ? "· 글자 카라오케" : "")}
              <br>key <code>{Esc(key)}</code>
            </p>
            <p class="meta">번역: {langLinks}
              · <a href="{Routes.Base}/song?key={Url(key)}&lang={Url(selectedLang)}&tags={(showTags ? 0 : 1)}">
                  타임태그 {(showTags ? "숨기기" : "보기")}</a>
              · <a href="{Routes.Base}/raw?key={Url(key)}">원문(.lrc)</a></p>
            <p class="meta">{ExternalLinks(entry, meaning, links)}</p>
            {SongActions(key, csrf, love ?? LoveState.NotConnected, links, spotify)}
            </div>
            </div>
            {MeaningCard(entry, meaning, csrf, meaningEnabled, meaningSources ?? [])}
            {body}

            <h2>편집</h2>
            <p class="meta">저장하면 <code>origin=user</code>로 기록되어 각 기기의 자동 검색 결과가
            이 가사를 덮어쓰지 못합니다. 형식은 확장 LRC 그대로 유지하세요.</p>
            <form method="post" action="{Routes.Base}/song/edit">
              <input type="hidden" name="key" value="{Esc(key)}">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <textarea name="lrc" spellcheck="false">{Esc(entry.Lrc)}</textarea>
              <div class="inline" style="margin-top:.5rem"><button type="submit">저장</button></div>
            </form>

            """, "search");
    }

    /// <summary>
    /// 대시보드의 Last.fm 계정 연결. <c>MUSEBASE_LASTFM_SECRET</c>이 없으면 <paramref name="link"/>가
    /// null로 와서 카드 자체를 그리지 않는다 — 눌러도 안 되는 것을 보여 주지 않는다.
    ///
    /// 연결은 <b>폼이 아니라 링크</b>다. CSP <c>form-action 'self'</c>가 외부 도메인으로의
    /// 폼 제출을 막기 때문이다(눌러도 조용히 아무 일도 안 일어난다).
    /// </summary>
    /// <summary>
    /// 의미 생성 엔진 카드 — <b>지금 무엇으로 쓰고 있는지</b>를 먼저 보여 주고, 그 자리에서 바꾼다.
    ///
    /// 예전에는 어느 모델이 쓰이는지 화면에 아예 없었고, 바꾸려면 서버에 들어가
    /// <c>server.env</c>를 고치고 재시작해야 했다 — 모델을 비교해 보려는 작업엔 너무 무겁다.
    ///
    /// API 키는 <b>끝 네 글자만</b> 보여 주고, 빈 칸으로 저장하면 기존 값을 그대로 둔다
    /// (모델만 바꾸려는데 매번 긴 키를 다시 치게 할 수는 없다).
    /// </summary>
    private static string MeaningEngineCardHtml(MeaningEngineCard? card, string csrf)
    {
        if (card is null) return "";

        string Engine(string id, string label) =>
            $"<option value=\"{Esc(id)}\"{(card.Engine == id ? " selected" : "")}>{Esc(label)}</option>";

        var state = card.Engine == "none"
            ? "<span class=\"meta\">꺼짐 — 엔진을 고르고 API 키를 넣으면 켜집니다.</span>"
            : card.HasKey
                ? $"<span class=\"meta\">지금 쓰는 모델: <b>{Esc(card.Engine)}</b> / <code>{Esc(card.Model)}</code></span>"
                : $"<span class=\"warn\">{Esc(card.Engine)}를 골랐지만 API 키가 없어 꺼져 있습니다.</span>";

        var origin = card.Overridden
            ? $"""
              <form method="post" action="{Routes.Base}/meanings/engine/reset" class="inline" style="margin:0"
                    data-busy data-confirm="화면에서 저장한 엔진·키·모델을 지우고 server.env 설정으로 되돌릴까요?">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">환경변수로 되돌리기</button>
              </form>
              """
            : "<span class=\"meta\">지금은 <code>server.env</code> 값을 그대로 쓰고 있습니다.</span>";

        string Key(string name, string? hint) =>
            $"""<input type="password" name="{name}" placeholder="{(hint is null ? "API 키" : $"넣어 둔 키 {Esc(hint)} — 비워 두면 유지")}" autocomplete="off">""";

        return $"""
            <h2>의미 생성 엔진</h2>
            <p>{state}</p>
            <form method="post" action="{Routes.Base}/meanings/engine" class="inline" data-busy>
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <select name="engine">
                {Engine("openrouter", "OpenRouter (모델 자유 선택)")}
                {Engine("gemini", "Google Gemini")}
                {Engine("none", "끔")}
              </select>
              {Key("openRouterKey", card.OpenRouterKeyHint)}
              <input type="text" name="openRouterModel" value="{Esc(card.OpenRouterModel)}"
                     placeholder="OpenRouter 모델 (예: anthropic/claude-opus-5)">
              {Key("geminiKey", card.GeminiKeyHint)}
              <input type="text" name="geminiModel" value="{Esc(card.GeminiModel)}"
                     placeholder="Gemini 모델 (예: gemini-2.5-flash-lite)">
              <button type="submit">저장</button>
            </form>
            <p class="meta">저장하면 <b>다음 생성부터 바로</b> 적용됩니다(재시작 불필요). {origin}<br>
            ⚠ 여기 넣은 API 키는 <b>DB에 평문으로</b> 저장되어 백업 파일에도 들어갑니다 —
            그게 싫으면 화면에 넣지 말고 <code>server.env</code>만 쓰세요.</p>
            """;
    }

    /// <summary>
    /// 번역 엔진 카드. 엔진 목록은 <b>레지스트리에서 만든다</b> — 코어에 엔진이 늘면 이 화면도
    /// 저절로 따라 늘어야 한다(의미 카드는 두 개를 손으로 적어 두었다).
    ///
    /// 키 입력칸은 그 엔진이 키를 쓸 때만 그린다. 엔진마다 값을 따로 보관하므로 엔진을 바꿔도
    /// 넣어 둔 키는 남는다(설정 화면의 규칙과 같다).
    /// </summary>
    private static string TranslationEngineCardHtml(TranslationEngineCard? card, string csrf)
    {
        if (card is null) return "";

        var chain = card.Chain.Count > 1
            ? " · 체인 <b>" + Esc(string.Join(" → ", card.Chain)) + "</b>"
            : "";
        var state = card.Enabled
            ? $"지금 번역 엔진: <b>{Esc(card.EngineName)}</b>"
              + (card.Model.Length == 0 ? "" : $" / <b>{Esc(card.Model)}</b>")
              + $" → <b>{Esc(card.Lang)}</b>{chain}"
            : "번역 엔진이 <b>꺼져 있습니다</b> — 엔진을 고르고 그 엔진의 키를 넣으세요.";

        // 폴백은 **키가 필요한 엔진만** 고를 수 있다 — 무키 공개 엔진으로 자동으로 넘어가면
        // 가사 수천 줄이 조용히 공개 서버로 나간다. 체크박스는 꺼져 있으면 전송되지 않으므로
        // 숨은 마커로 "지우기"를 표현한다(매직 문자열이 필요 없다).
        var fallback = card.FallbackChoices.Count == 0
            ? "<span class=\"meta\">폴백으로 쓸 수 있는 엔진이 없습니다(키가 필요한 엔진만 후보입니다).</span>"
            : "<div class=\"srcpick\">" + string.Concat(card.FallbackChoices.Select(id =>
                $"<label><input type=\"checkbox\" name=\"fallback\" value=\"{Esc(id)}\""
                + (card.FallbackOn.Contains(id) ? " checked" : "") + $"> {Esc(id)}</label>"))
              + "</div>";

        var usage = card.Usage.Count == 0 ? "" :
            "<p class=\"meta\">이번 달 서버가 보낸 문자: "
            + string.Join(" · ", card.Usage.Select(u =>
                u.Free is { } free
                    ? $"{Esc(u.Engine)} {u.Used:N0} / {free:N0}"
                      + (u.Used >= free ? " <b class=\"bad\">(한도 도달)</b>" : "")
                    : $"{Esc(u.Engine)} {u.Used:N0}"))
            + " — 기기가 직접 번역한 분은 여기 안 잡힙니다(근사치).</p>";

        var origin = card.Overridden
            ? $"""
              <form method="post" action="{Routes.Base}/translate/engine/reset" class="inline" style="display:inline-flex"
                    data-confirm="화면에서 저장한 번역 설정을 지우고 server.env 값으로 되돌립니다. 계속할까요?">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">환경변수로 되돌리기</button>
              </form>
              """
            : "<span class=\"meta\">지금은 <code>server.env</code> 값을 그대로 쓰고 있습니다.</span>";

        // 엔진 목록은 레지스트리 순서 그대로 + 맨 끝에 "끔".
        var engines = string.Concat(TranslatorRegistry.All.Select(d =>
            $"<option value=\"{Esc(d.Id)}\"{(d.Id == card.Engine ? " selected" : "")}>{Esc(d.DisplayName)}</option>"))
            + $"<option value=\"{TranslatorRegistry.None}\""
            + $"{(card.Engine == TranslatorRegistry.None ? " selected" : "")}>끔</option>";

        string Key(string name, string label, string? hint)
        {
            var placeholder = hint is null ? label : $"{label} {hint} — 비워 두면 유지";
            return $"<input type=\"password\" name=\"{name}\" autocomplete=\"off\" "
                 + $"placeholder=\"{Esc(placeholder)}\">";
        }

        return $"""
            <h2>가사 번역 엔진</h2>
            <p>{state}</p>
            <form method="post" action="{Routes.Base}/translate/engine" class="inline" data-busy>
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <select name="engine">{engines}</select>
              <input type="text" name="lang" value="{Esc(card.Lang)}" style="min-width:6rem"
                     placeholder="대상 언어 (KO)">
              {Key("deeplKey", "DeepL 키", card.DeeplKeyHint)}
              {Key("googleKey", "Google 키", card.GoogleKeyHint)}
              {Key("openRouterKey", "OpenRouter 키", card.OpenRouterKeyHint)}
              <input type="text" name="openRouterModel" value="{Esc(card.OpenRouterModel ?? "")}"
                     placeholder="OpenRouter 모델 (예: google/gemini-2.5-flash)">
              <input type="text" name="libreEndpoint" value="{Esc(card.LibreEndpoint ?? "")}"
                     placeholder="LibreTranslate 주소 (자체 호스팅)">
              {Key("libreKey", "LibreTranslate 키", card.LibreKeyHint)}
              <input type="text" name="myMemoryEmail" value="{Esc(card.MyMemoryEmail ?? "")}"
                     placeholder="MyMemory 이메일 (선택)">
              <input type="hidden" name="fallbackSubmitted" value="1">
              <button type="submit">저장</button>
            </form>
            <p class="meta">주 엔진이 막히면 이어받을 엔진(적힌 순서대로):</p>
            {fallback}
            {usage}
            <p class="meta">저장하면 <b>다음 실행부터 바로</b> 적용됩니다(재시작 불필요). {origin}<br>
            대상 언어는 DeepL식 대문자로 적습니다(<code>KO</code>·<code>EN-US</code>·<code>ZH-HANT</code>) —
            기기와 같은 표기여야 같은 줄을 두 번 번역하지 않습니다 · 캐시에 쌓인 줄 {card.CacheRows:N0}개<br>
            ⚠ 여기 넣은 API 키는 <b>DB에 평문으로</b> 저장되어 백업 파일에도 들어갑니다 —
            그게 싫으면 <code>server.env</code>만 쓰세요.<br>
            ⚠ 번역 캐시는 <b>엔진을 구분하지 않습니다</b> — 엔진을 바꿔도 이미 캐시된 줄은
            옛 엔진의 번역이 그대로 쓰입니다(비용에는 이득, 품질 비교에는 방해).</p>
            {(card.Warning is null ? "" : $"<p class=\"warn\">{Esc(card.Warning)}</p>")}
            """;
    }

    /// <summary>
    /// Spotify 카드. Last.fm과 달리 <b>콜백 주소를 앱 대시보드에 미리 등록</b>해야 하므로,
    /// 넣어야 할 값을 그대로 띄워 복사하게 한다 — 이걸 안 적어 두면 승인이 조용히 실패한다.
    /// </summary>
    private static string SpotifyCard(SpotifyLink? link, string csrf)
    {
        if (link is null) return "";

        var register = $"""
            <p class="meta">Spotify 개발자 대시보드의 <b>Redirect URI</b>에 이 주소를 등록해야 합니다:
            <code>{Esc(link.Callback)}</code><br>
            Development Mode는 <b>앱 소유자에게 Premium 구독</b>을 요구하고 인가 사용자는 5명까지입니다(2026-02 정책).</p>
            """;

        if (string.IsNullOrEmpty(link.User))
            return $"""
                <p class="meta"><a href="{Routes.Base}/spotify/connect">Spotify 계정 연결 →</a>
                — 연결하면 좋아요가 Last.fm과 Spotify 양쪽에 반영됩니다.</p>
                {register}
                """;

        return $"""
            <div class="actions">
              <form method="post" action="{Routes.Base}/spotify/sync" class="inline" style="margin:0" data-busy>
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">Spotify 좋아요 동기화</button>
              </form>
              <form method="post" action="{Routes.Base}/spotify/disconnect" class="inline" style="margin:0">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">Spotify 연결 해제</button>
              </form>
            </div>
            <p class="meta">연결됨: <b>{Esc(link.User)}</b> ·
            <b>동기화</b>는 라이브러리를 통째로 받아 곡 목록의 Spotify 칩에 반영하고
            트랙 URI도 함께 채웁니다(저쪽에서 뺀 곡도 함께 내립니다).</p>
            {register}
            """;
    }

    private static string LastFmCard(LastFmLink? link, string csrf)
    {
        if (link is null) return "";

        if (string.IsNullOrEmpty(link.User))
            return $"""
                <p class="meta"><a href="{Routes.Base}/lastfm/connect">Last.fm 계정 연결 →</a>
                — 연결하면 곡 상세에서 좋아요를 켜고 끌 수 있습니다.</p>
                """;

        return $"""
            <div class="actions">
              <form method="post" action="{Routes.Base}/lastfm/sync" class="inline" style="margin:0" data-busy>
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">Last.fm 좋아요 동기화</button>
              </form>
              <form method="post" action="{Routes.Base}/lastfm/disconnect" class="inline" style="margin:0">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">Last.fm 연결 해제</button>
              </form>
            </div>
            <p class="meta">연결됨: <b>{Esc(link.User)}</b> ·
            <b>동기화</b>는 좋아요 목록을 통째로 받아 곡 목록의 즐겨찾기 칩에 반영합니다(저쪽에서 해제한 곡도 함께 내립니다) ·
            last.fm 설정 &gt; Applications에서도 권한을 회수할 수 있습니다.</p>
            """;
    }

    /// <summary>
    /// 곡에서 밖으로 나가는 링크. 의미 카드가 아니라 <b>머리말 아래</b>에 둔다 —
    /// Tunefind·YouTube는 의미의 출처가 아니라 곡을 더 보러 가는 통로이고, 의미가 비었을 때
    /// 카드가 안내하는 "위 링크"가 실제로 위에 있어야 말이 맞는다.
    ///
    /// Musixmatch·Genius·Last.fm은 <b>확인한 주소가 있으면 그것을</b> 쓴다(없으면 검색으로 강등).
    /// </summary>
    private static string ExternalLinks(LyricsEntry entry, MeaningEntry? meaning, SongLinks? links)
    {
        var targets = new (string Label, string Url)[]
        {
            ("Last.fm", MeaningLinks.LastFm(entry.Title, entry.Artist, links?.LastFmUrl)),
            ("Tunefind", MeaningLinks.Tunefind(entry.Title, entry.Artist)),
            ("YouTube", MeaningLinks.YouTube(entry.Title, entry.Artist)),
            ("Musixmatch", MeaningLinks.Musixmatch(entry.Title, entry.Artist, meaning?.MusixmatchUrl)),
            ("Genius", MeaningLinks.Genius(entry.Title, entry.Artist, meaning?.GeniusUrl)),
        };

        return string.Join(" · ", targets.Select(t =>
            $"<a href=\"{Esc(t.Url)}\" target=\"_blank\" rel=\"noopener noreferrer\">{Esc(t.Label)}</a>"));
    }

    /// <summary>
    /// 곡에 대해 할 수 있는 일을 <b>한 줄에 모은다</b> — 좋아요 · 삭제 · 광고 표시 · 커버 다시 찾기.
    ///
    /// 예전에는 가사 편집창 <b>아래</b>에 흩어져 있어, 곡을 보다가 뭘 하려면 긴 가사를 지나
    /// 스크롤해야 했다. 곡 머리말 옆이 이것들이 있을 자리다.
    ///
    /// 위험한 둘(삭제·광고)은 <c>danger</c>로 칠하고 설명을 아래 한 줄에 붙인다 —
    /// 버튼만 넉 줄로 늘어놓으면 무엇이 되돌릴 수 없는 것인지 구분이 안 된다.
    /// </summary>
    private static string SongActions(
        string key, string csrf, LoveState love, SongLinks? links, SpotifyState? spotify)
    {
        string Form(string action, string label, string cls = "", string extra = "") => $"""
            <form method="post" action="{action}" class="inline" style="margin:0"{extra}>
              <input type="hidden" name="key" value="{Esc(key)}">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <button{(cls.Length == 0 ? "" : $" class=\"{cls}\"")} type="submit">{label}</button>
            </form>
            """;

        var source = links?.CoverSource is { } src ? $" (지금: {Esc(src)})" : "";
        return $"""
            <div class="actions">
              {LoveForm(key, csrf, love, spotify)}
              {Form(Routes.Base + "/song/cover", "커버 다시 찾기", extra: " data-busy")}
              {Form(Routes.Base + "/song/ad", "광고로 표시", "danger",
                    " data-confirm=\"이 제목을 광고로 표시할까요? 가사를 지우고 앞으로 등록·검색도 막습니다(대시보드에서 되돌릴 수 있습니다).\"")}
              {Form(Routes.Base + "/song/delete", "이 곡 삭제", "danger",
                    " data-confirm=\"이 곡의 가사를 지울까요? 되돌릴 수 없습니다(다음 재생 때 다시 검색해 채웁니다).\"")}
            </div>
            <p class="meta"><b>광고로 표시</b>는 이 제목을 차단합니다(가사를 지우고 이후 등록·검색도 막음 —
            되돌리기는 대시보드에서). <b>삭제</b>는 이 곡만 지우며, 다음에 재생하면 다시 검색해 채웁니다.
            <b>커버</b>는 한 번 못 찾으면 다시 찾지 않으니{source} 곡명을 고친 뒤 여기서 다시 시켜 주세요.</p>
            """;
    }

    /// <summary>
    /// Last.fm 좋아요 토글. 계정을 연결하지 않았으면 아무것도 그리지 않는다 —
    /// 눌러도 안 되는 버튼을 보여 주면 사람을 헷갈리게 한다.
    ///
    /// <b>모르는 상태(조회 실패)를 "좋아요 안 함"으로 그리지 않는다.</b> 그러면 이미 좋아요한 곡을
    /// 다시 눌러 꺼 버리게 된다 — 그때는 확인만 다시 시킨다.
    ///
    /// <c>data-busy</c>라 기존 스크립트가 스피너를 돌리고 히스토리도 늘리지 않는다.
    /// </summary>
    private static string LoveForm(string key, string csrf, LoveState love, SpotifyState? spotify = null)
    {
        if (!love.Connected) return "";

        var (label, extra, on) = love.Known
            ? (love.Loved ? "♥ 좋아요 해제" : "♡ 좋아요", love.Loved ? "0" : "1", love.Loved)
            : ("♡ 좋아요 확인", "1", false);

        var note = love.Known ? "" : "<span class=\"meta\">Last.fm 상태를 확인하지 못했습니다.</span>";

        // 좋아요는 Last.fm과 Spotify 양쪽에 반영된다 — 지금 저쪽이 어떤지도 같이 보여 준다.
        // "모름"과 "저장 안 됨"을 구별한다(Last.fm과 같은 이유).
        note += spotify switch
        {
            null or { Connected: false } => "",
            { Known: false } => "<span class=\"meta\">Spotify 상태를 확인하지 못했습니다.</span>",
            { Saved: true } => "<span class=\"meta ok\">Spotify에 저장됨</span>",
            _ => "<span class=\"meta\">Spotify에 없음</span>",
        };
        return $"""
            <form method="post" action="{Routes.Base}/song/love" class="inline" style="margin:0" data-busy>
              <input type="hidden" name="key" value="{Esc(key)}">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <input type="hidden" name="on" value="{extra}">
              <button class="love{(on ? " on" : "")}" type="submit">{label}</button>
              {note}
            </form>
            """;
    }

    /// <summary>
    /// 가사 위에 붙는 "이 곡의 의미" 카드. 의미가 없으면 생성 버튼만 보인다.
    ///
    /// <b>출처 표기는 의무다</b> — Wikipedia 본문은 CC BY-SA고 Genius·Last.fm도 링크 표기를
    /// 요구하므로 요약과 항상 함께 렌더한다.
    /// </summary>
    private static string MeaningCard(
        LyricsEntry entry, MeaningEntry? meaning, string csrf, bool enabled,
        IReadOnlyList<(string Id, string Label, bool Checked)> sources)
    {
        var key = entry.Key ?? "";

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
              <form method="post" action="{Routes.Base}/song/meaning" class="inline" data-busy>
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
                "<p class=\"meta\">외부 자료를 찾지 못했습니다 — 위의 외부 링크에서 직접 확인해 보세요.</p>",
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
            {button}
            """;
    }

    // ---- 조각 ----

    /// <summary>곡 목록 표의 열 이름 — 표를 만드는 곳이 여럿이라 한 군데서 정한다.</summary>
    private static readonly string[] SongHeaders =
        ["곡", "아티스트", "출처", "줄", "번역", "의미", "올린 기기", "갱신"];

    /// <summary>
    /// 곡 한 줄. <b>한 항목이 한 줄에 들어오게</b> 짧은 열은 전부 <c>nowrap</c>으로 묶고,
    /// 길어질 수 있는 곡·아티스트만 폭을 제한해 말줄임한다(전체 값은 <c>title</c>로 남긴다).
    /// 예전에는 기기 이름이 IP면 두 줄로 접혀 표가 들쭉날쭉했다.
    ///
    /// 아티스트는 링크다 — 그 아티스트의 곡을 다 보려면 이름을 정확히 다시 타이핑해야 했다.
    /// </summary>
    private static Func<SongRow, string> SongRowHtml(TimeZoneInfo tz) => r => $"""
        <tr><td class="ell" title="{Esc(r.Title)}">{SongLink(r.Key, r.Title)}</td>
        <td class="ell" title="{Esc(r.Artist)}">{ArtistLink(r.Artist)}</td>
        <td class="nowrap">{Esc(r.Service ?? "-")}</td>
        <td class="nowrap">{r.LineCount}{(r.HasInlineTimeTags ? " ●" : "")}</td>
        <td class="nowrap">{Esc(r.Langs.Length == 0 ? "-" : string.Join(",", r.Langs))}</td>
        <td class="nowrap">{MeaningCell(r.MeaningStatus)}</td>
        <td class="nowrap">{Esc(r.UpdatedBy ?? "-")}</td>
        <td class="nowrap">{Esc(AdminTime.ToLocal(r.UpdatedAt, tz))}</td></tr>
        """;

    /// <summary>아티스트 이름을 그 아티스트 검색으로 건다(검색이 제목·아티스트 부분 일치라 그대로 맞는다).</summary>
    private static string ArtistLink(string? artist) =>
        string.IsNullOrWhiteSpace(artist)
            ? Esc(artist)
            : $"<a href=\"{Routes.Base}/search?q={Url(artist!)}\">{Esc(artist)}</a>";

    private static string SongLink(string? key, string text) =>
        string.IsNullOrEmpty(key) ? Esc(text) : $"<a href=\"{Routes.Base}/song?key={Url(key)}\">{Esc(text)}</a>";

    /// <summary>
    /// 미스 행 한 줄. 그때는 없었어도 <b>지금은 서버에 있을 수 있어</b>, 있으면 곡으로 바로 간다
    /// (없으면 예전처럼 검색으로 보낸다).
    /// </summary>
    private static Func<MissRow, string> MissRowHtml(TimeZoneInfo tz) => r =>
    {
        var action = string.IsNullOrEmpty(r.Key)
            ? $"<a href=\"{Routes.Base}/search?q={Url(r.Title)}\">검색</a>"
            : $"<a href=\"{Routes.Base}/song?key={Url(r.Key)}\">가사 보기</a>";
        return $"""
            <tr><td>{SongLink(r.Key, r.Title)}</td><td>{Esc(r.Artist)}</td><td>{r.Count}</td><td>{r.Devices}</td>
            <td class="nowrap">{Esc(AdminTime.ToLocal(r.LastAt, tz))}</td>
            <td class="nowrap">{action}</td></tr>
            """;
    };

    /// <summary>
    /// 차단한 광고 제목 표. 되돌리는 버튼을 같이 둔다 — 잘못 눌러 진짜 곡을 막았을 때
    /// 서버에 들어가지 않고도 풀 수 있어야 한다.
    /// </summary>
    private static string AdTable(IReadOnlyList<AdTitleRow> rows, string csrf, TimeZoneInfo tz) => Table(
        ["차단한 제목", "표시 당시 아티스트", "표시 시각", ""],
        rows.Select(r => $"""
            <tr><td>{Esc(r.Title)}</td><td>{Esc(r.Artist)}</td>
            <td class="nowrap">{Esc(AdminTime.ToLocal(r.AddedAt, tz))}</td>
            <td><form method="post" action="{Routes.Base}/ads/remove" class="inline" style="margin:0">
              <input type="hidden" name="titleKey" value="{Esc(r.TitleKey)}">
              <input type="hidden" name="csrf" value="{Esc(csrf)}">
              <button type="submit">해제</button>
            </form></td></tr>
            """),
        "차단한 광고가 없습니다 — 광고가 가사로 올라오면 곡 상세에서 [광고로 표시]를 누르세요.");

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
