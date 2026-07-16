// Musebase.Browser — PlaybackViewState를 WebSocket으로 방송하고 정적 웹 디스플레이를 서빙하는
// 서버(Phase 1). 계약: contracts/playback-view-state.md (System.Text.Json 기본 = PascalCase).
//
// 엔드포인트:
//   GET /healthz  — 상태 확인
//   WS  /ws       — PlaybackViewState JSON 방송 구독(접속 즉시 현재 상태 1회 수신)
//   GET /         — wwwroot/ 정적 웹 디스플레이
//
// 실행: dotnet run                      (기본 http://localhost:5123)
//       dotnet run -- --demo            (샘플 가사 순환 방송)
//       dotnet run -- --urls http://0.0.0.0:5123   (LAN 공개 시)

using Musebase.Browser;

var demo = args.Contains("--demo");
var builder = WebApplication.CreateBuilder(args.Where(static a => a != "--demo").ToArray());

// 기본 포트 5123 — --urls 인자나 ASPNETCORE_URLS 환경변수로 재정의 가능.
if (builder.Configuration["urls"] is null &&
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://localhost:5123");
}

builder.Services.AddSingleton<StateBroadcaster>();

var app = builder.Build();
var broadcaster = app.Services.GetRequiredService<StateBroadcaster>();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.MapGet("/healthz", () => "ok");

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket endpoint — connect via ws://");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await broadcaster.HandleClientAsync(socket, context.RequestAborted);
});

if (demo)
{
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    _ = DemoLoop.RunAsync(broadcaster, lifetime.ApplicationStopping);
}

app.Run();
