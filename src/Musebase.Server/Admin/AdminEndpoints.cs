using System.Diagnostics;

namespace Musebase.Server;

/// <summary>관리자 페이지 설정(환경변수에서 읽는다).</summary>
public sealed record AdminOptions(
    string Token,
    TimeZoneInfo TimeZone,
    IReadOnlyDictionary<string, string> DeviceLabels,
    bool LogLookups,
    int RetentionDays,
    int YieldWindowSeconds)
{
    /// <summary>
    /// `MUSEBASE_ADMIN_TOKEN`(없으면 `MUSEBASE_TOKEN`), `MUSEBASE_TZ`(기본 Asia/Seoul),
    /// `MUSEBASE_DEVICES`, `MUSEBASE_LOG_LOOKUPS`, `MUSEBASE_LOOKUP_RETENTION_DAYS`,
    /// `MUSEBASE_YIELD_WINDOW_SECONDS`(0이면 번역 양보 힌트를 주지 않는다).
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
            YieldWindowSeconds: yieldWindow);
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
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    /// <summary>`/admin/list`가 한 번에 보여 주는 최대 행 수 — 넘으면 화면에 그렇게 밝힌다.</summary>
    private const int FullRows = 200;

    public static void MapAdmin(
        this WebApplication app, LyricsStore store, AdminOptions options,
        Musebase.Core.Meaning.SongMeaningService meanings, MeaningOptions meaningOptions)
    {
        // 스크립트는 딱 하나(제출 스피너)뿐이라 'unsafe-inline' 대신 **그 해시만** 허용한다 —
        // 다른 스크립트는 여전히 한 줄도 실행되지 않는다(AdminHtml.BusyScript 참고).
        var Csp = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; "
                + $"script-src {AdminHtml.ScriptCsp}";

        IResult Html(string html) =>
            Results.Text(html, "text/html; charset=utf-8");

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
                context.Response.Headers["Content-Security-Policy"] = Csp;
            await next();
        });

        // ---- 로그인 ----

        app.MapGet("/admin/logout", (HttpResponse res) =>
        {
            res.Cookies.Delete(CookieName, new CookieOptions { Path = "/admin" });
            return Html(AdminPages.Login("로그아웃했습니다."));
        });

        app.MapPost("/admin/login", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();
            if (!TokenMatches(form["token"].ToString(), options.Token))
                return Html(AdminPages.Login("토큰이 맞지 않습니다."));
            SetCookie(res);
            return Results.Redirect("/admin");
        });

        // ---- 대시보드 ----

        app.MapGet("/admin", (HttpRequest req, HttpResponse res, string? token, string? notice) =>
        {
            // ?token=…로 들어오면 쿠키를 굽고 주소창을 정리한다(토큰이 히스토리·로그에 남지 않도록).
            if (!string.IsNullOrEmpty(token))
            {
                if (!TokenMatches(token!, options.Token)) return Html(AdminPages.Login("토큰이 맞지 않습니다."));
                SetCookie(res);
                return Results.Redirect("/admin");
            }
            if (!LoggedIn(req)) return Html(AdminPages.Login());

            var now = DateTimeOffset.UtcNow;
            return Html(AdminPages.Dashboard(
                BuildDashboard(req, now, AdminPages.DashboardRows), now, options.TimeZone, notice));
        });

        // 대시보드의 한 섹션을 전부 보여 준다. 섹션마다 라우트를 파지 않고 ?view= 하나로 받는다.
        app.MapGet("/admin/list", (HttpRequest req, string? view) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            if (view is null || !AdminPages.ListViews.TryGetValue(view, out var heading))
                return Results.Redirect("/admin");

            var now = DateTimeOffset.UtcNow;
            var model = BuildDashboard(req, now, FullRows);
            var table = AdminPages.ListTable(view, model, options.TimeZone);
            var count = view switch
            {
                "lookups" => model.Recent.Count,
                "misses" => model.TopMisses.Count,
                "untranslated" => model.WithoutTranslation.Count,
                "duplicates" => model.DuplicateCandidates.Count,
                _ => model.CleanedMatches.Count,
            };
            var note = count < FullRows ? null : $"최대 {FullRows}건까지 보여 줍니다";
            return Html(AdminPages.ListPage(heading, table, count, note));
        });

        // ---- 검색·열람 ----

        app.MapGet("/admin/search", (HttpRequest req, string? q, string? meaning) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            var filter = string.IsNullOrWhiteSpace(meaning) ? null : meaning;
            return Html(AdminPages.SearchPage(
                q, store.Search(q, limit: 200, meaning: filter), options.TimeZone, filter));
        });

        app.MapGet("/admin/song", (HttpRequest req, string? key, string? lang, string? tags, string? notice) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            if (string.IsNullOrWhiteSpace(key)) return Results.Redirect("/admin/search");

            var entry = store.GetByKey(key!);
            if (entry is null) return Html(AdminPages.SearchPage(key, Array.Empty<SongRow>(), options.TimeZone));

            var langs = AdminLrc.TranslationTags(entry.Lrc);
            var selected = string.IsNullOrWhiteSpace(lang) ? langs.FirstOrDefault() : lang;
            var showTags = tags != "0";

            return Html(AdminPages.SongPage(
                entry, AdminLrc.ToDisplayLines(entry.Lrc, selected), langs, selected, showTags,
                AdminAuth.Csrf(options.Token, Cookie(req) ?? ""), options.TimeZone, notice,
                store.GetMeaningByKey(entry.Key ?? ""), meanings.IsEnabled,
                meaningOptions.SelectableSources()
                    .Select(s => (s.Id, MeaningOptions.SourceLabel(s.Id), s.Default))
                    .ToList()));
        });

        app.MapGet("/admin/raw", (HttpRequest req, string? key) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key!);
            return entry is null ? Results.NotFound() : Results.Text(entry.Lrc, "text/plain; charset=utf-8");
        });

        // ---- 편집·삭제 ----

        app.MapPost("/admin/song/edit", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var lrc = form["lrc"].ToString();
            var existing = store.GetByKey(key);
            if (existing is null || string.IsNullOrWhiteSpace(lrc)) return Results.Redirect("/admin/search");

            // origin=user로 저장 → 병합 정책이 각 기기의 자동 검색 결과로부터 이 편집본을 보호한다.
            store.Upsert(existing with { Lrc = lrc, Origin = LyricsEntry.OriginUser, Service = "사용자 편집" },
                updatedBy: "admin", out _);
            return Results.Redirect($"/admin/song?key={Uri.EscapeDataString(key)}&notice={Uri.EscapeDataString("저장했습니다.")}");
        });

        app.MapPost("/admin/song/delete", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            if (!string.IsNullOrWhiteSpace(key)) store.Delete(key);
            return Results.Redirect("/admin/search");
        });

        // ---- 곡의 의미 ----
        // 생성은 **사람이 누를 때만** 일어난다. 자동 생성을 두지 않는 이유는 쿼타·비용이
        // 예측 가능해야 하고, 실패가 조용히 쌓이면 안 되기 때문이다.

        app.MapPost("/admin/song/meaning", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            var key = form["key"].ToString();
            var entry = string.IsNullOrWhiteSpace(key) ? null : store.GetByKey(key);
            if (entry is null) return Results.Redirect("/admin/search");

            // 화면에서 고른 자료원(체크박스). 하나도 안 고르면 설정값으로 만든다.
            var picked = form["src"].Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
            var notice = await GenerateMeaningAsync(entry.Key ?? key, entry.Title, entry.Artist, picked);
            return Results.Redirect(
                $"/admin/song?key={Uri.EscapeDataString(key)}&notice={Uri.EscapeDataString(notice)}");
        });

        app.MapPost("/admin/meanings/backfill", async (HttpRequest req) =>
        {
            if (!LoggedIn(req)) return Html(AdminPages.Login());
            var form = await req.ReadFormAsync();
            if (!AdminAuth.VerifyCsrf(form["csrf"].ToString(), options.Token, Cookie(req) ?? ""))
                return Results.Json(new ApiError("csrf"), statusCode: StatusCodes.Status400BadRequest);

            if (!meanings.IsEnabled)
                return Results.Redirect($"/admin?notice={Uri.EscapeDataString("의미 엔진이 구성되지 않았습니다.")}");

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
                if (status == Musebase.Core.Meaning.SongMeaning.Ok) ok++;
                else if (status == Musebase.Core.Meaning.SongMeaning.NoSource) none++;
                else failed++;
            }

            var summary = stopped
                ? $"{done}곡 처리 후 중단 — 생성 {ok} · 자료 없음 {none} · 실패 {failed}. "
                  + "쿼타·네트워크 문제로 보입니다. 남은 곡은 손대지 않았으니 잠시 후 다시 눌러 주세요."
                : $"{targets.Count}곡 처리 — 생성 {ok} · 자료 없음 {none} · 실패 {failed}";
            return Results.Redirect($"/admin?notice={Uri.EscapeDataString(summary)}");
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
                Csrf: AdminAuth.Csrf(options.Token, Cookie(req) ?? ""));
        }

        MeaningSummary MeaningSummaryOf()
        {
            var (ok, none, failed) = store.MeaningStats();
            // "아직 안 해 본 곡"은 백필 버튼이 실제로 처리할 대상 수다(상한까지만 센다).
            var pending = store.SongsWithoutMeaning(meaningOptions.BackfillLimit).Count;
            return new MeaningSummary(ok, none, failed, pending, meanings.IsEnabled);
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
