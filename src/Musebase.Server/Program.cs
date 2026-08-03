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
// 요청줄에는 쿼리스트링이 포함된다 — 관리자 부트스트랩 URL(`/admin?token=…`)의 토큰이
// journalctl에 그대로 남지 않도록 Hosting 로그를 Warning으로 낮춘다.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);

var app = builder.Build();
using var store = new LyricsStore(dbPath);
var admin = AdminOptions.FromEnvironment(token!);

// 곡의 의미 — 키가 없으면 서비스가 꺼진 상태로 만들어지고 아무 데도 영향을 주지 않는다.
var meaningOptions = MeaningOptions.FromEnvironment();
var meanings = meaningOptions.BuildService();

app.MapAdmin(store, admin, meanings, meaningOptions);

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

app.MapGet("/v1/healthz", () => Results.Text("ok"));

app.MapGet("/v1/lyrics", (HttpRequest request, string? title, string? artist) =>
{
    if (!Authorized(request)) return Unauthorized();
    if (string.IsNullOrWhiteSpace(title)) return Results.Json(new ApiError("title required"), statusCode: 400);

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

app.MapPut("/v1/lyrics", async (HttpRequest request) =>
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

    // 어느 기기가 올렸는지 남긴다(관리자 화면의 "올린 기기").
    var saved = store.Upsert(incoming, updatedBy: AdminEndpoints.DeviceOf(request, admin), out var rejection);
    return saved is null
        ? Results.Json(rejection!, statusCode: StatusCodes.Status202Accepted)
        : Results.Ok(saved);
});

app.MapGet("/v1/stats", (HttpRequest request) =>
    !Authorized(request) ? Unauthorized() : Results.Ok(store.Stats()));

// 곡의 의미 — 앱은 조회만 한다. 생성은 관리자 화면에서만 일어난다(쿼타·비용을 사람이 통제).
app.MapGet("/v1/meaning", (HttpRequest request, string? title, string? artist) =>
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

app.Run();
return 0;
