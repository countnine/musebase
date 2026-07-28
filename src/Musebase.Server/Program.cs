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

const int MaxBodyBytes = 256 * 1024;

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

var app = builder.Build();
using var store = new LyricsStore(dbPath);

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
    return found is null ? Results.NotFound() : Results.Ok(found);
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

    var saved = store.Upsert(incoming, updatedBy: null, out var rejection);
    return saved is null
        ? Results.Json(rejection!, statusCode: StatusCodes.Status202Accepted)
        : Results.Ok(saved);
});

app.MapGet("/v1/stats", (HttpRequest request) =>
    !Authorized(request) ? Unauthorized() : Results.Ok(store.Stats()));

app.Run();
return 0;
