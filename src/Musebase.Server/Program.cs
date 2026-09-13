using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Musebase.Server;

// 개인용 가사 캐시 서버. 계약은 contracts/lyrics-api.md (v1).
//
// 배포는 루프백만 리슨하고 외부 노출은 `tailscale serve`가 담당한다(deploy/README.md).
// 환경변수:
//   MUSEBASE_TOKEN  필수 — 공유 Bearer 토큰
//   MUSEBASE_DB     선택 — SQLite 경로(기본 ./lyrics.db)
// CLI:
//   --import <translations.db>   기존 클라이언트 캐시를 흡수하고 종료(시드용)
//   --hash-password <비밀번호>    MUSEBASE_ADMIN_PASSWORD에 넣을 해시를 찍고 종료

const int MaxBodyBytes = 256 * 1024;
// 양보 힌트에 실어 보내는 재조회 간격. 클라이언트는 이 값을 자기 상한으로 clamp한다.
const int YieldRetryAfterMs = 3000;

// --hash-password: 설정 파일에 평문을 두지 않아도 되도록 해시를 만들어 준다.
var hashIndex = Array.IndexOf(args, "--hash-password");
if (hashIndex >= 0)
{
    if (hashIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("사용법: Musebase.Server --hash-password <비밀번호>");
        return 2;
    }
    Console.WriteLine(AdminPassword.Hash(args[hashIndex + 1]));
    return 0;
}

var dbPath = Environment.GetEnvironmentVariable("MUSEBASE_DB") ?? "lyrics.db";

// --import 모드: 서버를 띄우지 않고 시드만 하고 끝낸다.
var importIndex = Array.IndexOf(args, "--import");
if (importIndex >= 0)
{
    if (importIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("사용법: Musebase.Server --import <translations.db>");
        return 2;
    }
    using var seedStore = new LyricsStore(dbPath);
    var (imported, skipped) = seedStore.ImportLegacyCache(args[importIndex + 1]);
    Console.WriteLine($"임포트 완료: {imported}곡 저장, {skipped}곡 건너뜀(병합 정책) → {dbPath}");
    return 0;
}

var token = Environment.GetEnvironmentVariable("MUSEBASE_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("MUSEBASE_TOKEN 환경변수가 필요합니다(공유 Bearer 토큰).");
    return 2;
}
var tokenBytes = Encoding.UTF8.GetBytes(token);

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // 계약이 camelCase다 — PlaybackViewState(PascalCase)와 다르니 주의.
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
// 요청줄에는 쿼리스트링이 포함된다 — 관리자 부트스트랩 URL(`/musebase?token=…`)의 토큰이
// journalctl에 그대로 남지 않도록 Hosting 로그를 Warning으로 낮춘다.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);

var app = builder.Build();
using var store = new LyricsStore(dbPath);
var admin = AdminOptions.FromEnvironment(token!);

// 곡의 의미 — 키가 없으면 서비스가 꺼진 상태로 만들어지고 아무 데도 영향을 주지 않는다.
// 환경변수 위에 DB 설정을 덮는다 — 관리 화면에서 엔진·모델을 바꾸면 재시작 없이 반영된다.
var meaningSettings = new MeaningSettings(store, MeaningOptions.FromEnvironment());
var meaningGenerator = new MeaningGenerator(store, meaningSettings);
var extras = new SongExtrasService(
    store, new CoverArt(),
    meaningSettings.Current.LastFmAccount(),
    meaningSettings.Current.SpotifyAccount());

app.MapAdmin(store, admin, meaningSettings, meaningGenerator, extras);

// 보존 기간이 지난 조회 기록 정리 — 시작 시 1회 + 하루 1회.
_ = Task.Run(async () =>
{
    var timer = new PeriodicTimer(TimeSpan.FromHours(24));
    do
    {
        try { store.PruneLookups(admin.RetentionDays); }
        catch (Exception e) { app.Logger.LogWarning("조회 기록 정리 실패: {Message}", e.Message); }
    }
    while (await timer.WaitForNextTickAsync());
});

/// 공유 토큰 검사 — 타이밍 공격을 피하려 고정시간 비교를 쓴다.
bool Authorized(HttpRequest request)
{
    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var provided = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
    return CryptographicOperations.FixedTimeEquals(provided, tokenBytes);
}

IResult Unauthorized() => Results.Json(new ApiError("unauthorized"), statusCode: StatusCodes.Status401Unauthorized);

app.MapGet(Routes.Api + "/healthz", () => Results.Text("ok"));

app.MapGet(Routes.Api + "/lyrics", (HttpRequest request, string? title, string? artist) =>
{
    if (!Authorized(request)) return Unauthorized();
    if (string.IsNullOrWhiteSpace(title)) return Results.Json(new ApiError("title required"), statusCode: 400);

    // 광고로 표시된 제목은 여기서 끝난다. 조회 기록도 남기지 않는다 — 광고는 곡이 아니라서
    // 미스로 세면 히트율과 "미스 상위(채울 후보)" 목록이 둘 다 오염된다.
    if (store.IsAdTitle(title!))
        return Results.Json(new NotFoundBody("ad", false, 0, Ad: true), statusCode: 404);

    var found = store.Get(title!, artist ?? "");
    var device = AdminEndpoints.DeviceOf(request, admin);

    // 미스일 때만, 최근에 다른 기기도 같은 곡을 물었는지 본다(번역 양보 힌트).
    // **기록을 남기기 전에** 판정해 자기 행이 끼어들 여지를 없앤다. 조회 기록이 꺼져 있으면
    // 판단 근거가 없으므로 힌트도 주지 않는다.
    var pending = false;
    if (found is null && admin.LogLookups && admin.YieldWindowSeconds > 0)
    {
        try
        {
            var since = DateTimeOffset.UtcNow.AddSeconds(-admin.YieldWindowSeconds).ToString(LyricsStore.TimeFormat);
            pending = store.RecentlyMissedByOther(title!, device, since);
        }
        catch (Exception e) { app.Logger.LogWarning("양보 판정 실패: {Message}", e.Message); }
    }

    // 관리자 화면용 조회 기록 — 실패해도 조회 자체를 깨뜨리지 않는다.
    if (admin.LogLookups)
    {
        try
        {
            store.LogLookup(title!, artist ?? "", found?.Match ?? LyricsEntry.MatchMiss, found?.Key,
                device, request.Headers.UserAgent.ToString());
        }
        catch (Exception e) { app.Logger.LogWarning("조회 기록 실패: {Message}", e.Message); }
    }

    if (found is not null) return Results.Ok(found);
    return pending
        ? Results.Json(new NotFoundBody("not found", true, YieldRetryAfterMs), statusCode: 404)
        : Results.NotFound();
});

app.MapPut(Routes.Api + "/lyrics", async (HttpRequest request) =>
{
    if (!Authorized(request)) return Unauthorized();
    if (request.ContentType is null || !request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new ApiError("content-type must be application/json"), statusCode: 415);
    if (request.ContentLength > MaxBodyBytes)
        return Results.Json(new ApiError("body too large"), statusCode: 413);

    LyricsEntry? incoming;
    try
    {
        request.EnableBuffering();
        incoming = await request.ReadFromJsonAsync<LyricsEntry>();
    }
    catch (Exception e)
    {
        return Results.Json(new ApiError($"invalid json: {e.Message}"), statusCode: 400);
    }

    if (incoming is null || string.IsNullOrWhiteSpace(incoming.Title) || string.IsNullOrWhiteSpace(incoming.Lrc))
        return Results.Json(new ApiError("title and lrc required"), statusCode: 400);

    // 광고로 표시된 제목은 등록하지 않는다. **구버전 앱이 힌트를 몰라도 여기서 막힌다** —
    // 차단의 실효는 이 줄에 있고, 조회 쪽 힌트는 헛도는 검색을 아끼기 위한 것이다.
    if (store.IsAdTitle(incoming.Title))
        return Results.Json(new ApiError("ad"), statusCode: StatusCodes.Status202Accepted);

    // 어느 기기가 올렸는지 남긴다(관리자 화면의 "올린 기기").
    var saved = store.Upsert(incoming, updatedBy: AdminEndpoints.DeviceOf(request, admin), out var rejection);
    return saved is null
        ? Results.Json(rejection!, statusCode: StatusCodes.Status202Accepted)
        : Results.Ok(saved);
});

app.MapGet(Routes.Api + "/stats", (HttpRequest request) =>
    !Authorized(request) ? Unauthorized() : Results.Ok(store.Stats()));

// 곡의 의미 — 앱은 조회만 한다. 생성은 관리자 화면에서만 일어난다(쿼타·비용을 사람이 통제).
app.MapGet(Routes.Api + "/meaning", (HttpRequest request, string? title, string? artist) =>
{
    if (!Authorized(request)) return Unauthorized();
    if (string.IsNullOrWhiteSpace(title)) return Results.Json(new ApiError("title required"), statusCode: 400);

    // `insufficient`도 404다 — 문단은 있지만 "파악하기 어렵다"는 고백이라 곡 해설로 띄우면 안 된다.
    var found = store.GetMeaning(title!, artist ?? "");
    if (found is null || found.Status != MeaningEntry.StatusOk) return Results.NotFound();

    // 원문 전체(sources)는 무겁고 앱에 필요 없다 — 출처 표기만 계산해 싣는다.
    return Results.Ok(found with
    {
        Sources = "",
        Attribution = MeaningMapper.Attribution(found.Sources),
    });
});

// 앱에서 의미 만들기. **사람이 누를 때만 일어난다**는 원칙은 그대로고(자동 생성은 여전히 없다),
// 누르는 자리가 관리자 화면 하나에서 각 기기로 늘어난 것이다(ADR-0007 결정 4 참고).
// 비용이 드는 유일한 쓰기 경로라 `MUSEBASE_MEANING_ALLOW_CLIENT=0`으로 막을 수 있다.
app.MapPost(Routes.Api + "/meaning", async (HttpRequest request, string? title, string? artist) =>
{
    if (!Authorized(request)) return Unauthorized();
    if (string.IsNullOrWhiteSpace(title)) return Results.Json(new ApiError("title required"), statusCode: 400);

    if (!meaningSettings.Current.AllowClientGeneration)
        return Results.Json(new ApiError("client generation disabled"), statusCode: StatusCodes.Status403Forbidden);
    if (!meaningGenerator.IsEnabled)
        return Results.Json(new ApiError("meaning engine not configured"), statusCode: StatusCodes.Status503ServiceUnavailable);

    // 광고는 곡이 아니다 — 조회에서 막는 것과 같은 이유로 생성에도 토큰을 쓰지 않는다.
    if (store.IsAdTitle(title!))
        return Results.Json(new ApiError("ad"), statusCode: 404);

    // 의미는 가사 행에 붙는다(같은 key). 서버에 없는 곡은 만들 자리가 없다 —
    // 앱은 방금 그 곡의 가사를 받아 띄운 상태이므로 정상 경로에서는 늘 있다.
    var found = store.Get(title!, artist ?? "");
    if (found?.Key is not { Length: > 0 } key) return Results.Json(new ApiError("song not found"), statusCode: 404);

    var status = await meaningGenerator.GenerateAsync(key, found.Title, found.Artist);

    // 만들어졌으면 조회와 **같은 모양**으로 돌려준다 — 앱이 다시 GET 하지 않아도 되게.
    if (status == MeaningEntry.StatusOk && store.GetMeaningByKey(key) is { } made)
        return Results.Ok(made with { Sources = "", Attribution = MeaningMapper.Attribution(made.Sources) });

    // 만들지 못한 이유는 앱이 사람에게 그대로 설명해야 하므로 상태를 실어 보낸다.
    return Results.Json(new MeaningNotMade(status), statusCode: StatusCodes.Status202Accepted);
});

// ---- 곡에 딸린 것들(커버·좋아요) ----
// 앱 제어판이 재생 중인 곡 하나에 대해 필요한 값을 **한 번에** 받아 가는 자리다.
// 커버는 아직 안 찾아본 곡이면 여기서 찾는다 — 관리자 화면을 열어 본 곡에만 커버가 생기면
// 앱에서는 거의 늘 비어 보인다. 한 곡당 한 번이고 못 찾은 것도 기억하므로 값이 싸다.

/// title·artist로 저장된 곡을 찾는다. 없으면 null(만들 자리가 없다).
LyricsEntry? Locate(string? title, string? artist) =>
    string.IsNullOrWhiteSpace(title) ? null : store.Get(title!, artist ?? "");

app.MapGet(Routes.Api + "/song", async (HttpRequest request, string? title, string? artist) =>
{
    if (!Authorized(request)) return Unauthorized();
    if (string.IsNullOrWhiteSpace(title)) return Results.Json(new ApiError("title required"), statusCode: 400);

    var entry = Locate(title, artist);
    if (entry is null) return Results.NotFound();

    var links = await extras.ResolveAsync(entry);
    var love = await extras.LoveAsync(entry);
    return Results.Ok(SongExtrasBody.From(links, love, await extras.SpotifyAsync(entry)));
});

app.MapPost(Routes.Api + "/song/cover", async (HttpRequest request, string? title, string? artist) =>
{
    if (!Authorized(request)) return Unauthorized();
    var entry = Locate(title, artist);
    if (entry is null) return Results.NotFound();

    // 곡명을 고친 뒤 다시 시키는 경로다 — 기억해 둔 "없음"을 먼저 지운다.
    store.ForgetCover(entry.Key ?? "");
    await extras.RefindCoverAsync(entry);

    var links = await extras.ResolveAsync(entry);
    return Results.Ok(SongExtrasBody.From(links, await extras.LoveAsync(entry), await extras.SpotifyAsync(entry)));
});

app.MapPost(Routes.Api + "/song/love", async (HttpRequest request, string? title, string? artist, string? on) =>
{
    if (!Authorized(request)) return Unauthorized();
    var entry = Locate(title, artist);
    if (entry is null) return Results.NotFound();

    if (!extras.LoveConnected)
        return Results.Json(new ApiError("lastfm not connected"), statusCode: StatusCodes.Status503ServiceUnavailable);

    var loved = on != "0";
    var result = await extras.SetLovedAsync(entry, loved);

    // Last.fm이 실패하면 실패다. Spotify만 실패한 경우는 아래 본문의 spotifyKnown=false로 알린다 —
    // 절반이라도 반영된 것을 통째로 오류로 만들면 앱이 화면을 되돌려 더 헷갈린다.
    if (!result.LastFm)
        return Results.Json(new ApiError("lastfm write failed"), statusCode: StatusCodes.Status502BadGateway);

    // 방금 쓴 값을 그대로 돌려준다 — 앱이 확인차 다시 묻지 않아도 되게.
    return Results.Ok(SongExtrasBody.From(
        store.GetSongLinks(entry.Key ?? ""),
        new LoveState(true, true, loved),
        result.Spotify switch
        {
            null => SpotifyState.NotConnected,
            true => new SpotifyState(true, true, loved),
            false => new SpotifyState(true, false, false),   // 반영 못 했다 — 상태를 모른다
        }));
});

app.Run();
return 0;
