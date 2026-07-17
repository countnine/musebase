using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Musebase.Engine;

namespace Musebase.Browser;

/// <summary>
/// <see cref="BrowserDisplayServer"/> 호스팅 옵션.
/// </summary>
/// <param name="Port">리슨 포트(기본 5123). 0이면 OS가 임의 포트를 배정하며 <see cref="BrowserDisplayServer.Urls"/>로 확인한다.</param>
/// <param name="ListenLan">true면 모든 인터페이스(0.0.0.0)에 바인딩해 같은 LAN의 다른 기기가 접속할 수 있다. 기본은 localhost 전용.</param>
/// <param name="Log">서버 로그(리슨 주소, 요청 오류 등)를 받을 콜백. null이면 로그를 내보내지 않는다.</param>
public sealed record BrowserDisplayOptions(
    int Port = 5123,
    bool ListenLan = false,
    Action<string>? Log = null);

/// <summary>
/// 다른 앱(WPF/Android/콘솔)에 내장 가능한 브라우저 디스플레이 서버.
/// 인프로세스로 Kestrel을 띄워 정적 웹 디스플레이(임베디드 리소스)와
/// <c>/ws</c> WebSocket 방송을 서빙하고, <see cref="Publish"/>로
/// <see cref="PlaybackViewState"/>를 모든 구독자에게 흘려보낸다.
///
/// 사용 예:
/// <code>
/// await using var server = await BrowserDisplayServer.StartAsync(new BrowserDisplayOptions());
/// coordinator.StateChanged += state => server.Publish(state);
/// </code>
///
/// 엔드포인트: GET /healthz(상태 확인), WS /ws(상태 방송 구독), GET /(웹 디스플레이).
/// </summary>
public sealed class BrowserDisplayServer : IAsyncDisposable
{
    // 임베디드 index.html — 참조하는 exe에서 호스팅해도 파일 복사 없이 서빙된다.
    private static readonly Lazy<byte[]> IndexHtml = new(LoadIndexHtml);

    private readonly WebApplication _app;

    private BrowserDisplayServer(WebApplication app, StateBroadcaster broadcaster, IReadOnlyList<string> urls)
    {
        _app = app;
        Broadcaster = broadcaster;
        Urls = urls;
    }

    /// <summary>실제 리슨 중인 주소들(예: <c>http://localhost:5123</c>). Port=0이면 배정된 포트가 반영된다.</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>내부 방송기 — 같은 어셈블리의 CLI(데모 루프)가 직접 쓴다.</summary>
    internal StateBroadcaster Broadcaster { get; }

    /// <summary>서버가 정지 절차에 들어가면 취소되는 토큰.</summary>
    internal CancellationToken ApplicationStopping => _app.Lifetime.ApplicationStopping;

    /// <summary>서버를 시작한다. 반환 시점에 리슨이 시작된 상태다.</summary>
    public static Task<BrowserDisplayServer> StartAsync(
        BrowserDisplayOptions options, CancellationToken ct = default)
        => StartAsync(options, args: [], ct);

    /// <summary>
    /// CLI용 오버로드: <paramref name="args"/>(예: <c>--urls</c>)가 옵션보다 우선한다.
    /// </summary>
    internal static async Task<BrowserDisplayServer> StartAsync(
        BrowserDisplayOptions options, string[] args, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 로그는 호스트 앱의 콜백으로만 내보낸다(내장 시 콘솔이 없을 수 있다).
        builder.Logging.ClearProviders();
        if (options.Log is { } log)
            builder.Logging.AddProvider(new DelegateLoggerProvider(log));

        // 주소 우선순위: --urls 인자 / ASPNETCORE_URLS 환경변수 > 옵션(Port·ListenLan).
        if (builder.Configuration["urls"] is null &&
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
        {
            var host = options.ListenLan ? "0.0.0.0" : "localhost";
            builder.WebHost.UseUrls($"http://{host}:{options.Port}");
        }

        builder.Services.AddSingleton<StateBroadcaster>();

        var app = builder.Build();
        var broadcaster = app.Services.GetRequiredService<StateBroadcaster>();

        app.UseWebSockets();

        app.MapGet("/healthz", static () => "ok");

        // 정적 웹 디스플레이 — 임베디드 리소스에서 서빙(wwwroot 파일 복사 불필요).
        app.MapGet("/", static () =>
            Results.Bytes(IndexHtml.Value, "text/html; charset=utf-8"));
        app.MapGet("/index.html", static () =>
            Results.Bytes(IndexHtml.Value, "text/html; charset=utf-8"));

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

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new BrowserDisplayServer(app, broadcaster, app.Urls.ToArray());
    }

    /// <summary>
    /// 새 표시 상태를 모든 구독자에게 방송한다. 어느 스레드에서 호출해도 안전하며 블로킹하지 않는다.
    /// </summary>
    public void Publish(PlaybackViewState state) => Broadcaster.Publish(state);

    /// <summary>CLI용: 서버가 종료 신호(Ctrl+C 등)를 받을 때까지 대기한다.</summary>
    internal Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>서버를 정지하고 자원을 해제한다.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] LoadIndexHtml()
    {
        using var stream = typeof(BrowserDisplayServer).Assembly
            .GetManifestResourceStream("Musebase.Browser.wwwroot.index.html")
            ?? throw new InvalidOperationException(
                "임베디드 리소스 'Musebase.Browser.wwwroot.index.html'이 없습니다 — csproj의 EmbeddedResource 항목을 확인하세요.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>호스트 앱 콜백(<see cref="BrowserDisplayOptions.Log"/>)으로 로그를 전달하는 최소 프로바이더.</summary>
    private sealed class DelegateLoggerProvider(Action<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new DelegateLogger(log);

        public void Dispose()
        {
        }

        private sealed class DelegateLogger(Action<string> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                var message = formatter(state, exception);
                log(exception is null ? message : $"{message} — {exception}");
            }
        }
    }
}
