using System.Diagnostics;

namespace Musebase.Server;

/// <summary>관리자 페이지 설정(환경변수에서 읽는다).</summary>
public sealed record AdminOptions(
    string Token,
    TimeZoneInfo TimeZone,
    IReadOnlyDictionary<string, string> DeviceLabels,
    bool LogLookups,
    int RetentionDays,
    int YieldWindowSeconds,
    string User = "admin",
    string? Password = null)
{
    /// <summary>비밀번호를 정해 뒀는가 — 로그인 화면이 어떤 칸을 보여 줄지 가른다.</summary>
    public bool HasPassword => !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// `MUSEBASE_ADMIN_TOKEN`(없으면 `MUSEBASE_TOKEN`), `MUSEBASE_TZ`(기본 Asia/Seoul),
    /// `MUSEBASE_DEVICES`, `MUSEBASE_LOG_LOOKUPS`, `MUSEBASE_LOOKUP_RETENTION_DAYS`,
    /// `MUSEBASE_YIELD_WINDOW_SECONDS`(0이면 번역 양보 힌트를 주지 않는다),
    /// `MUSEBASE_ADMIN_USER`(기본 admin), `MUSEBASE_ADMIN_PASSWORD`(해시 또는 평문).
    ///
    /// 비밀번호를 정해도 <b>토큰 로그인은 계속 살려 둔다</b> — 비밀번호를 잊거나 해시를 잘못 넣으면
    /// 들어갈 길이 없어지기 때문이다. 토큰은 어차피 앱이 API에 쓰는 값이라 새 비밀이 늘지도 않는다.
    /// </summary>
    public static AdminOptions FromEnvironment(string apiToken)
    {
        var tzId = Environment.GetEnvironmentVariable("MUSEBASE_TZ");
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(tzId) ? "Asia/Seoul" : tzId!); }
        catch (Exception) { tz = TimeZoneInfo.Utc; } // tzdata가 없는 환경 — UTC로 표시

        var retention = int.TryParse(Environment.GetEnvironmentVariable("MUSEBASE_LOOKUP_RETENTION_DAYS"), out var d)
            ? Math.Clamp(d, 1, 3650) : 90;

        var yieldWindow = int.TryParse(Environment.GetEnvironmentVariable("MUSEBASE_YIELD_WINDOW_SECONDS"), out var y)
            ? Math.Clamp(y, 0, 600) : 30;

        return new AdminOptions(
            Token: Environment.GetEnvironmentVariable("MUSEBASE_ADMIN_TOKEN") is { Length: > 0 } t ? t : apiToken,
            TimeZone: tz,
            DeviceLabels: DeviceLabel.ParseLabels(Environment.GetEnvironmentVariable("MUSEBASE_DEVICES")),
            LogLookups: Environment.GetEnvironmentVariable("MUSEBASE_LOG_LOOKUPS") != "0",
            RetentionDays: retention,
            YieldWindowSeconds: yieldWindow,
            User: Environment.GetEnvironmentVariable("MUSEBASE_ADMIN_USER") is { Length: > 0 } u ? u : "admin",
            Password: Environment.GetEnvironmentVariable("MUSEBASE_ADMIN_PASSWORD"));
    }
}

/// <summary>
/// 관리자 화면의 유일한 I/O 계층 — 라우팅·쿠키·DB 조회를 여기서만 한다.
/// HTML 생성은 <see cref="AdminPages"/>(순수 함수)가 맡는다.
/// `/admin/*`은 사람용 UI라 `/v1` 계약(`contracts/lyrics-api.md`) 밖이며 예고 없이 바뀔 수 있다.
/// </summary>
public static class AdminEndpoints
{
    private const string CookieName = "musebase_admin";

    /// <summary>Last.fm 승인 플로우의 1회용 논스. 관리자 쿠키와 달리 <b>SameSite=Lax</b>여야 한다.</summary>
    private const string StateCookie = "musebase_lastfm_state";

    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    /// <summary>303 See Other — 이 프레임워크에 기본 헬퍼가 없어 직접 만든다.</summary>
    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }

    /// <summary>`/admin/list`가 한 번에 보여 주는 최대 행 수 — 넘으면 화면에 그렇게 밝힌다.</summary>
    private const int FullRows = 200;

    public static void MapAdmin(
        this WebApplication app, LyricsStore store, AdminOptions options,
        Musebase.Core.Meaning.SongMeaningService meanings, MeaningOptions meaningOptions)
    {
        // 스크립트는 딱 하나(제출 스피너)뿐이라 'unsafe-inline' 대신 **그 해시만** 허용한다 —
        // 다른 스크립트는 여전히 한 줄도 실행되지 않는다(AdminHtml.BusyScript 참고).
        // connect-src가 필요한 이유: 그 스크립트가 폼을 fetch로 보낸다. 기본값 'none'이면
        // 조용히 막혀 버튼만 잠긴 채 아무 일도 일어나지 않는다. 대상은 같은 출처뿐이다.
        // img-src를 열지 않으면 default-src 'none' 때문에 커버가 **조용히** 안 뜬다(콘솔에만 남는다).
        // 호스트는 CoverArt가 실제로 부르는 두 곳으로 한정한다 — 새 자료원을 더하면 여기도 같이 는다.
        var Csp = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; "
                + "connect-src 'self'; "
                + "img-src 'self' https://*.mzstatic.com https://*.dzcdn.net; "
                + $"script-src {AdminHtml.ScriptCsp}";

        IResult Html(string html) =>
            Results.Text(html, "text/html; charset=utf-8");

        // POST 뒤에는 303이 맞다 — 302는 "다음 요청의 메서드"를 규정하지 않아 브라우저마다 다르다.
        // 303은 반드시 GET으로 가라는 뜻이라 새로고침이 POST를 되풀이하지 않는다.
        static IResult SeeOther(string location) => new SeeOtherResult(location);

        var lastfm = meaningOptions.LastFmAccount();
        var covers = new CoverArt();

        string? Cookie(HttpRequest req) => req.Cookies.TryGetValue(CookieName, out var v) ? v : null;

        bool LoggedIn(HttpRequest req) =>
            AdminAuth.Verify(Cookie(req), options.Token, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        void SetCookie(HttpResponse res)
        {
            var expires = DateTimeOffset.UtcNow.Add(CookieLifetime);
            res.Cookies.Append(CookieName, AdminAuth.Sign(options.Token, expires.ToUnixTimeSeconds()),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,          // tailscale serve가 HTTPS 종단이다
                    SameSite = SameSiteMode.Strict,
                    Path = "/admin",
                    Expires = expires,
                });
        }

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin"))
            {
                context.Response.Headers["Content-Security-Policy"] = Csp;

                // **뒤로 가기로 낡은 화면이 되살아나면 안 된다.** 캐시 지시가 없으면 브라우저가
                // 뒤로 가기를 캐시에서 그리는데, 방금 만든 의미나 방금 고친 가사가 사라진 것처럼
                // 보인다 — 사람은 작업이 실패한 줄 알고 다시 누른다. 관리자 화면은 전부 지금
                // 상태를 봐야 하는 화면이므로 저장하지 않는다.
                context.Response.Headers.CacheControl = "no-store";
            }
            await next();
        });

        // ---- 로그인 ----

        app.MapGet("/admin/logout", (HttpResponse res) =>
        {
            res.Cookies.Delete(CookieName, new CookieOptions { Path = "/admin" });
            return Html(AdminPages.Login("로그아웃했습니다.", options.HasPassword));
        });

        app.MapPost("/admin/login", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();

            var token = form["token"].ToString();
            var user = form["user"].ToString();
            var password = form["password"].ToString();

            var ok = token.Length > 0
                ? TokenMatches(token, options.Token)
                : string.Equals(user.Trim(), options.User, StringComparison.Ordinal)
                  && AdminPassword.Verify(password, options.Password);

            if (!ok)
            {
                // 온라인 추측을 느리게 만든다. 테일넷 안이라 위험은 낮지만 값이 싸다.
                await Task.Delay(700);
                return Html(AdminPages.Login(
                    token.Length > 0 ? "토큰이 맞지 않습니다." : "아이디 또는 비밀번호가 맞지 않습니다.",
                    options.HasPassword));
            }

            SetCookie(res);
            return SeeOther("/admin");
        });

        // ---- 대시보드 ----

        app.MapGet("/admin", (HttpRequest req, HttpResponse res, string? token, string? notice) =>
        {
            // ?token=…로 들어오면 쿠키를 굽고 주소창을 정리한다(토큰이 히스토리·로그에 남지 않도록).
            if (!string.IsNullOrEmpty(token))
            {
                if (!TokenMatches(token!, options.Token)) return Html(AdminPages.Login("토큰이 맞지 않습니다.", options.HasPassword));
                SetCookie(res);
                return SeeOther("/admin");
            }
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));

            var now = DateTimeOffset.UtcNow;
            return Html(AdminPages.Dashboard(
                BuildDashboard(req, now, AdminPages.DashboardRows), now, options.TimeZone, notice));
        });

        // 대시보드의 한 섹션을 전부 보여 준다. 섹션마다 라우트를 파지 않고 ?view= 하나로 받는다.
        app.MapGet("/admin/list", (HttpRequest req, string? view) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            if (view is null || !AdminPages.ListViews.TryGetValue(view, out var heading))
                return SeeOther("/admin");

            var now = DateTimeOffset.UtcNow;
            var model = BuildDashboard(req, now, FullRows);
            var table = AdminPages.ListTable(view, model, options.TimeZone);
            var count = view switch
            {
                "lookups" => model.Recent.Count,
                "misses" => model.TopMisses.Count,
                "untranslated" => model.WithoutTranslation.Count,
                "duplicates" => model.DuplicateCandidates.Count,
                "ads" => model.AdTitles?.Count ?? 0,
                _ => model.CleanedMatches.Count,
            };
            var note = count < FullRows ? null : $"최대 {FullRows}건까지 보여 줍니다";
            return Html(AdminPages.ListPage(heading, table, count, note));
        });

        // ---- 검색·열람 ----

        app.MapGet("/admin/search", (HttpRequest req, string? q, string? meaning) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var filter = string.IsNullOrWhiteSpace(meaning) ? null : meaning;
            return Html(AdminPages.SearchPage(
                q, store.Search(q, limit: 200, meaning: filter), options.TimeZone, filter));
        });

        app.MapGet("/admin/song", async (HttpRequest req, string? key, string? lang, string? tags, string? notice) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            if (string.IsNullOrWhiteSpace(key)) return SeeOther("/admin/search");

            var entry = store.GetByKey(key!);
            if (entry is null) return Html(AdminPages.SearchPage(key, Array.Empty<SongRow>(), options.TimeZone));

            var langs = AdminLrc.TranslationTags(entry.Lrc);
            var selected = string.IsNullOrWhiteSpace(lang) ? langs.FirstOrDefault() : lang;
            var showTags = tags != "0";

            var links = await ResolveLinksAsync(entry);
            var love = await LoveStateOf(entry);

            return Html(AdminPages.SongPage(
                entry, AdminLrc.ToDisplayLines(entry.Lrc, selected), langs, selected, showTags,
                AdminAuth.Csrf(options.Token, Cookie(req) ?? ""), options.TimeZone, notice,
                store.GetMeaningByKey(entry.Key ?? ""), meanings.IsEnabled,
                meaningOptions.SelectableSources()
                    .Select(s => (s.Id, MeaningOptions.SourceLabel(s.Id), s.Default))
                    .ToList(),
                links, love));
        });

        app.MapGet("/admin/raw", (HttpRequest req, string? key) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key!);
            return entry is null ? Results.NotFound() : Results.Text(entry.Lrc, "text/plain; charset=utf-8");
        });

        // ---- 편집·삭제 ----

        app.MapPost("/admin/song/edit", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var lrc = form["lrc"].ToString();
            var existing = store.GetByKey(key);
            if (existing is null || string.IsNullOrWhiteSpace(lrc)) return SeeOther("/admin/search");

            // origin=user로 저장 → 병합 정책이 각 기기의 자동 검색 결과로부터 이 편집본을 보호한다.
            store.Upsert(existing with { Lrc = lrc, Origin = LyricsEntry.OriginUser, Service = "사용자 편집" },
                updatedBy: "admin", out _);
            return SeeOther($"/admin/song?key={Uri.EscapeDataString(key)}&notice={Uri.EscapeDataString("저장했습니다.")}");
        });

        app.MapPost("/admin/song/delete", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            if (!string.IsNullOrWhiteSpace(key)) store.Delete(key);
            return SeeOther("/admin/search");
        });

        // ---- 광고 차단 ----
        // 자동 판정(AdSignals)은 재생 메타데이터에 기대는데, Spotify가 광고 플래그를 안 주는
        // 경로에서는 "광고 없이 음악을 감상하세요." 같은 행이 가사로 올라온다. 사람이 표시하면
        // 그 제목은 이후 조회·등록에서 막힌다.

        app.MapPost("/admin/song/ad", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key);
            if (entry is null) return SeeOther("/admin/search");

            store.AddAdTitle(entry.Title, entry.Artist);
            var notice = $"\"{entry.Title}\"을(를) 광고로 표시했습니다 — 가사를 지웠고 앞으로 등록되지 않습니다.";
            return SeeOther($"/admin?notice={Uri.EscapeDataString(notice)}");
        });

        app.MapPost("/admin/ads/remove", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var titleKey = form["titleKey"].ToString();
            if (!string.IsNullOrWhiteSpace(titleKey)) store.RemoveAdTitle(titleKey);
            return SeeOther($"/admin?notice={Uri.EscapeDataString("광고 표시를 해제했습니다.")}");
        });

        // ---- 커버 이미지 ----

        app.MapPost("/admin/song/cover", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key);
            if (entry is null) return SeeOther("/admin/search");

            store.ForgetCover(entry.Key ?? key);
            var found = await FindCoverAsync(entry);
            var notice = found is null ? "커버를 찾지 못했습니다." : $"커버를 찾았습니다({found.Source}).";
            return SeeOther($"/admin/song?key={Uri.EscapeDataString(key)}&notice={Uri.EscapeDataString(notice)}");
        });

        // ---- Last.fm 계정 연결 ----
        // 승인은 last.fm에서 일어나고 브라우저가 여기로 돌아온다. **관리자 쿠키는 SameSite=Strict라
        // 그 크로스사이트 이동에는 실리지 않는다** — 그대로 두면 콜백이 로그인 화면으로 떨어지고
        // 1회용 토큰이 날아간다. 그래서 콜백의 신원 증명은 아래 state 논스 쿠키(SameSite=Lax)로 한다.
        // 논스는 로그인한 관리자가 /connect를 눌렀을 때만 구워지므로 그 사람이 시작한 플로우임을 증명한다.

        app.MapGet("/admin/lastfm/connect", (HttpRequest req, HttpResponse res) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            if (!lastfm.CanConnect)
                return SeeOther($"/admin?notice={Uri.EscapeDataString("MUSEBASE_LASTFM_KEY와 MUSEBASE_LASTFM_SECRET이 필요합니다.")}");

            var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            res.Cookies.Append(StateCookie, nonce, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,   // Strict면 last.fm에서 돌아올 때 실리지 않는다
                Path = "/admin/lastfm",
                Expires = DateTimeOffset.UtcNow.AddMinutes(10),
            });

            // 콜백은 지금 요청의 출처로 만든다 — API 계정에 콜백을 미리 등록하지 않아도 된다.
            var origin = CallbackOrigin(
                req.Scheme, req.Host.Host, req.Host.ToString(), req.Headers["X-Forwarded-Proto"].ToString());
            return SeeOther(lastfm.AuthorizeUrl($"{origin}/admin/lastfm/callback"));
        });

        app.MapGet("/admin/lastfm/callback", async (HttpRequest req, HttpResponse res, string? token) =>
        {
            var nonce = req.Cookies.TryGetValue(StateCookie, out var v) ? v : null;
            res.Cookies.Delete(StateCookie, new CookieOptions { Path = "/admin/lastfm" });

            if (string.IsNullOrEmpty(nonce))
                return SeeOther($"/admin?notice={Uri.EscapeDataString("연결 요청이 만료됐습니다 — 다시 눌러 주세요.")}");
            if (string.IsNullOrWhiteSpace(token))
                return SeeOther($"/admin?notice={Uri.EscapeDataString("Last.fm이 승인을 거절했습니다.")}");

            var session = await lastfm.ExchangeTokenAsync(token!);
            if (session is null)
                return SeeOther($"/admin?notice={Uri.EscapeDataString("세션 키를 받지 못했습니다(토큰은 1회용입니다 — 다시 시도하세요).")}");

            store.SetSetting(LastFmAccount.SessionSetting, session.Value.Session);
            store.SetSetting(LastFmAccount.UserSetting, session.Value.User);
            return SeeOther($"/admin?notice={Uri.EscapeDataString($"Last.fm에 연결했습니다: {session.Value.User}")}");
        });

        app.MapPost("/admin/lastfm/disconnect", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            store.DeleteSetting(LastFmAccount.SessionSetting);
            store.DeleteSetting(LastFmAccount.UserSetting);
            return SeeOther($"/admin?notice={Uri.EscapeDataString("Last.fm 연결을 해제했습니다.")}");
        });

        app.MapPost("/admin/song/love", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key);
            if (entry is null) return SeeOther("/admin/search");

            var session = store.GetSetting(LastFmAccount.SessionSetting);
            var notice = string.IsNullOrEmpty(session)
                ? "Last.fm 계정이 연결돼 있지 않습니다."
                : await SetLovedAsync(entry, form["on"].ToString() != "0", session!);

            return SeeOther($"/admin/song?key={Uri.EscapeDataString(key)}&notice={Uri.EscapeDataString(notice)}");
        });

        async Task<string> SetLovedAsync(LyricsEntry entry, bool loved, string session)
        {
            var ok = await lastfm.SetLovedAsync(entry.Title, entry.Artist, loved, session);
            if (!ok) return "Last.fm에 반영하지 못했습니다(연결이 끊겼을 수 있습니다).";
            return loved ? "Last.fm 좋아요를 켰습니다." : "Last.fm 좋아요를 껐습니다.";
        }

        /// 커버를 찾아 저장한다. **못 찾아도 저장한다** — 그래야 화면을 열 때마다 다시 부르지 않는다.
        async Task<CoverImage?> FindCoverAsync(LyricsEntry entry)
        {
            var found = await covers.FindAsync(entry.Title, entry.Artist);
            store.SetCover(entry.Key ?? "", found?.Url, found?.Source);
            return found;
        }

        async Task<SongLinks> ResolveLinksAsync(LyricsEntry entry)
        {
            var links = store.GetSongLinks(entry.Key ?? "");
            if (links.CoverTried) return links;

            var found = await FindCoverAsync(entry);
            return links with { CoverUrl = found?.Url, CoverSource = found?.Source, CoverAt = "now" };
        }

        /// 좋아요 여부. **모르면 Known=false다** — 모르는 것을 "안 함"으로 그리면 이미 켜 둔 곡을 끄게 된다.
        async Task<LoveState> LoveStateOf(LyricsEntry entry)
        {
            var session = store.GetSetting(LastFmAccount.SessionSetting);
            var user = store.GetSetting(LastFmAccount.UserSetting);
            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(user)) return LoveState.NotConnected;

            var state = await lastfm.GetStateAsync(entry.Title, entry.Artist, user!);
            if (state is null) return new LoveState(true, false, false);

            // 정식 곡 주소는 알아낸 김에 기억해 둔다 — 다음부터는 규칙으로 만든 주소를 쓰지 않는다.
            if (state.Url is not null) store.SetLastFmUrl(entry.Key ?? "", state.Url);
            return new LoveState(true, true, state.Loved);
        }

        // ---- 곡의 의미 ----
        // 생성은 **사람이 누를 때만** 일어난다. 자동 생성을 두지 않는 이유는 쿼타·비용이
        // 예측 가능해야 하고, 실패가 조용히 쌓이면 안 되기 때문이다.

        app.MapPost("/admin/song/meaning", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key);
            if (entry is null) return SeeOther("/admin/search");

            // 화면에서 고른 자료원(체크박스). 하나도 안 고르면 설정값으로 만든다.
            var picked = form["src"].Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
            var notice = await GenerateMeaningAsync(entry.Key ?? key, entry.Title, entry.Artist, picked);
            return SeeOther(
                $"/admin/song?key={Uri.EscapeDataString(key)}&notice={Uri.EscapeDataString(notice)}");
        });

        app.MapPost("/admin/meanings/backfill", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login(null, options.HasPassword));
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            if (!meanings.IsEnabled)
                return SeeOther($"/admin?notice={Uri.EscapeDataString("의미 엔진이 구성되지 않았습니다.")}");

            var targets = store.SongsWithoutMeaning(meaningOptions.BackfillLimit);
            int ok = 0, none = 0, failed = 0, done = 0;
            var stopped = false;
            foreach (var (key, title, artist) in targets)
            {
                if (done > 0 && meaningOptions.BackfillDelayMs > 0)
                    await Task.Delay(meaningOptions.BackfillDelayMs);

                var status = await GenerateStatusAsync(key, title, artist);

                // 쿼타·네트워크 같은 일시적 실패면 여기서 멈춘다. 계속 돌아 봐야 남은 곡까지
                // 같은 벽에 부딪힐 뿐이고, 중단해도 아무것도 망가지지 않는다 — 저장을 안 했으므로
                // 다음에 다시 누르면 이 곡부터 그대로 이어진다.
                if (status == Musebase.Core.Meaning.SongMeaning.Retry) { stopped = true; break; }

                done++;
                // 자료 부족은 "자료 없음"과 같은 칸에 센다 — 둘 다 "의미를 만들지 못함"이다.
                if (status == Musebase.Core.Meaning.SongMeaning.Ok) ok++;
                else if (status is Musebase.Core.Meaning.SongMeaning.NoSource
                                or Musebase.Core.Meaning.SongMeaning.Insufficient) none++;
                else failed++;
            }

            var summary = stopped
                ? $"{done}곡 처리 후 중단 — 생성 {ok} · 자료 없음 {none} · 실패 {failed}. "
                  + "쿼타·네트워크 문제로 보입니다. 남은 곡은 손대지 않았으니 잠시 후 다시 눌러 주세요."
                : $"{targets.Count}곡 처리 — 생성 {ok} · 자료 없음 {none} · 실패 {failed}";
            return SeeOther($"/admin?notice={Uri.EscapeDataString(summary)}");
        });

        // 단건 생성 후 사람에게 보여 줄 한 줄.
        async Task<string> GenerateMeaningAsync(
            string key, string title, string artist, IReadOnlyList<string>? only = null)
        {
            if (!meanings.IsEnabled) return "의미 엔진이 구성되지 않았습니다(키를 확인하세요).";
            var status = await GenerateStatusAsync(key, title, artist, only);
            return status switch
            {
                Musebase.Core.Meaning.SongMeaning.Ok => "의미를 만들었습니다.",
                Musebase.Core.Meaning.SongMeaning.NoSource => "외부 자료를 찾지 못했습니다.",
                Musebase.Core.Meaning.SongMeaning.Insufficient =>
                    "자료가 부족해 의미를 판단하지 못했습니다 — 자료원을 바꿔 다시 시도해 보세요.",
                Musebase.Core.Meaning.SongMeaning.Retry =>
                    "일시적인 오류입니다(쿼타·네트워크). 저장하지 않았으니 잠시 후 다시 시도하세요.",
                _ => "생성에 실패했습니다(키를 확인하세요).",
            };
        }

        // 대시보드와 `/admin/list`가 같은 모델을 쓴다 — 행 수만 다르다.
        DashboardModel BuildDashboard(HttpRequest req, DateTimeOffset now, int rows)
        {
            var todayStart = AdminTime.TodayStartUtc(now, options.TimeZone);
            var weekStart = AdminTime.DaysAgoUtc(now, 7);

            return new DashboardModel(
                Stats: store.Stats(),
                DatabaseSizeBytes: store.DatabaseSizeBytes(),
                Today: store.HitRateSince(todayStart),
                Week: store.HitRateSince(weekStart),
                Recent: store.RecentLookups(rows),
                TopMisses: store.TopMisses(weekStart, rows),
                Devices: store.DeviceActivity(weekStart),
                Daily: store.DailyHitRate(weekStart),
                CleanedMatches: store.CleanedMatches(weekStart, rows),
                RecentUploads: store.RecentUploads(rows),
                WithoutTranslation: store.WithoutTranslation(rows),
                DuplicateCandidates: store.DuplicateCandidates(rows),
                Health: Health(options.RetentionDays),
                Diagnostics: Diagnostics(req, options),
                Meanings: MeaningSummaryOf(),
                MeaningSources: meanings.SourceNames,
                Csrf: AdminAuth.Csrf(options.Token, Cookie(req) ?? ""),
                AdTitles: store.AdTitles(rows),
                // 연결할 수 없는 구성이면 null — 카드를 아예 그리지 않는다.
                LastFm: lastfm.CanConnect
                    ? new LastFmLink(store.GetSetting(LastFmAccount.UserSetting))
                    : null);
        }

        MeaningSummary MeaningSummaryOf()
        {
            var (ok, none, failed, insufficient) = store.MeaningStats();
            // "아직 안 해 본 곡"은 백필 버튼이 실제로 처리할 대상 수다(상한까지만 센다).
            var pending = store.SongsWithoutMeaning(meaningOptions.BackfillLimit).Count;
            return new MeaningSummary(ok, none, failed, pending, meanings.IsEnabled, insufficient);
        }

        // 결과를 저장하고 status만 돌려준다. 실패·자료없음도 행으로 남겨 백필이 같은 곡을
        // 무한히 재시도하지 않게 한다 — 단 **일시적 실패는 예외다.** 쿼타 초과를 행으로
        // 남기면 한도가 회복된 뒤에도 그 곡은 영영 건너뛰어진다.
        async Task<string> GenerateStatusAsync(
            string key, string title, string artist, IReadOnlyList<string>? only = null)
        {
            // 소스를 골라 왔으면 이번 한 번만 그 조합으로 만든다(설정은 그대로 둔다).
            var service = only is { Count: > 0 } ? meaningOptions.BuildService(only) : meanings;
            var result = await service.BuildAsync(title, artist, meaningOptions.Lang);
            if (result.Status == Musebase.Core.Meaning.SongMeaning.Retry) return result.Status;

            // 곡 페이지 주소는 의미 소스와 별개다 — Musixmatch를 자료로 쓰지 않아도 링크는 정확해야 한다.
            var musixmatch = await meaningOptions.MusixmatchApi().FindAsync(title, artist);

            store.UpsertMeaning(
                MeaningMapper.ToEntry(key, title, artist, meaningOptions.Lang, result)
                with { MusixmatchUrl = musixmatch?.ShareUrl });
            return result.Status;
        }
    }

    /// <summary>
    /// last.fm이 브라우저를 되돌려 보낼 우리 쪽 출처.
    ///
    /// <b><c>req.Scheme</c>을 그대로 쓰면 안 된다.</b> `tailscale serve`가 HTTPS를 종단하고
    /// 앱에는 평문 HTTP로 넘기므로 실측에서 <c>http://oracle.…ts.net</c>이 나왔다 —
    /// 그 호스트의 80번에는 아무도 없어서 승인 후 돌아올 때 "서버에 연결할 수 없습니다"가 뜬다.
    /// 게다가 state 논스 쿠키가 <c>Secure</c>라 평문으로는 실리지도 않는다.
    ///
    /// 그래서 ① 프록시가 알려 준 <c>X-Forwarded-Proto</c>를 먼저 믿고,
    /// ② 없으면 <b>루프백이 아닌 이상 https</b>로 본다 — 관리자 쿠키가 이미 <c>Secure</c>라
    /// 이 화면 전체가 애초에 HTTPS를 전제로 한다(로컬 개발만 예외).
    /// </summary>
    /// <param name="host">호스트 이름만(포트 제외) — 루프백 판정용.</param>
    /// <param name="authority">주소에 실제로 넣을 <c>host[:port]</c>.</param>
    public static string CallbackOrigin(string scheme, string host, string authority, string? forwardedProto)
    {
        // 프록시가 여럿이면 "https, http"처럼 쉼표로 이어 붙는다 — 맨 앞이 원래 클라이언트 쪽이다.
        var proto = (forwardedProto ?? "").Split(',')[0].Trim();

        if (proto.Length == 0)
            proto = IsLoopback(host) ? scheme : "https";

        return $"{proto}://{authority}";
    }

    private static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    /// <summary>기기 라벨 계산(요청 헤더 → 이름). 조회 기록과 업로드 표기에 함께 쓴다.</summary>
    public static string DeviceOf(HttpRequest req, AdminOptions options) => DeviceLabel.Resolve(
        req.Headers["X-Musebase-Device"].ToString(),
        req.Headers.UserAgent.ToString(),
        req.Headers["X-Forwarded-For"].ToString(),
        req.HttpContext.Connection.RemoteIpAddress?.ToString(),
        options.DeviceLabels);

    private static bool TokenMatches(string provided, string expected) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided.Trim()),
            System.Text.Encoding.UTF8.GetBytes(expected));

    private static ServerHealth Health(int retentionDays)
    {
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/").AvailableFreeSpace; }
        catch (Exception) { /* 컨테이너 등 — 표시만 생략 */ }

        using var self = Process.GetCurrentProcess();
        return new ServerHealth(DateTimeOffset.UtcNow - self.StartTime.ToUniversalTime(),
            self.WorkingSet64, free, retentionDays);
    }

    /// <summary>
    /// 진단용 원값 — `tailscale serve` 뒤에서 어떤 헤더가 실제로 오는지 배포 한 번으로 확인하려는 것.
    /// 기기 이름 매핑(`MUSEBASE_DEVICES`)을 여기 보이는 IP로 채운다.
    /// </summary>
    private static IReadOnlyList<(string, string)> Diagnostics(HttpRequest req, AdminOptions options) =>
    [
        ("X-Forwarded-For", req.Headers["X-Forwarded-For"].ToString()),
        ("RemoteIpAddress", req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
        ("User-Agent", req.Headers.UserAgent.ToString()),
        ("Tailscale-User-Login", req.Headers["Tailscale-User-Login"].ToString()),
        ("이 요청의 기기 라벨", DeviceOf(req, options)),
        ("기기 매핑", options.DeviceLabels.Count == 0
            ? "(없음 — MUSEBASE_DEVICES 미설정)"
            : string.Join(", ", options.DeviceLabels.Select(kv => $"{kv.Key}={kv.Value}"))),
        ("조회 기록", options.LogLookups ? "켜짐" : "꺼짐 (MUSEBASE_LOG_LOOKUPS=0)"),
        ("표시 시간대", options.TimeZone.Id),
    ];
}
