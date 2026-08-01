using System.Net;
using System.Text;
using Musebase.Core;
using Musebase.Core.Search;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// <see cref="HttpRemoteLyricsCache"/> — 서버가 없거나 느리거나 이상해도 **앱이 멈추지 않는다**는
/// 계약을 지키는지 본다(조회 실패 = null, 예외 전파 없음, 반복 실패 시 시도 자체를 건너뜀).
/// </summary>
public class RemoteLyricsCacheTests
{
    private const string Lrc = "[ti:T]\n[00:01.00]hello\n[00:01.00][tr:ko]안녕\n";

    [Fact]
    public async Task Get_히트면_LRC를_파싱해_돌려준다()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK,
            $$"""{"key":"t|a","title":"T","artist":"A","lrc":{{System.Text.Json.JsonSerializer.Serialize(Lrc)}},"service":"LRCLIB","origin":"provider","langs":["ko"],"match":"exact"}""")));
        var cache = Create(handler);

        var result = await cache.GetAsync("T", "A");

        var lyrics = result.Lyrics;
        Assert.NotNull(lyrics);
        Assert.Single(lyrics!.Lines);
        Assert.Equal("LRCLIB", lyrics.Metadata.ServiceName);
        Assert.Equal("안녕", lyrics.Lines[0].Attachments.Translation("ko"));
        Assert.True(result.HasLanguage("KO")); // 서버가 알려 준 언어 목록도 함께 온다
    }

    [Fact]
    public async Task Get_404면_미스이고_예외가_없다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var result = await cache.GetAsync("T", "A");
        Assert.Null(result.Lyrics);
        Assert.False(result.Pending); // 본문 없는 404(구버전 서버)는 평범한 미스다
    }

    [Fact]
    public async Task Get_404_본문의_양보_힌트를_읽는다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.NotFound,
            """{"error":"not found","pending":true,"retryAfterMs":3000}"""))));

        var result = await cache.GetAsync("T", "A");

        Assert.Null(result.Lyrics);
        Assert.True(result.Pending);
        Assert.Equal(3000, result.RetryAfterMs);
    }

    [Fact]
    public async Task Get_서버가_죽어도_예외_대신_미스()
    {
        var cache = Create(new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))));
        Assert.Null((await cache.GetAsync("T", "A")).Lyrics);
    }

    [Fact]
    public async Task Get_응답이_깨져도_미스()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{ this is not json"))));
        Assert.Null((await cache.GetAsync("T", "A")).Lyrics);
    }

    [Fact]
    public async Task 연속_실패하면_회로가_열려_더는_요청하지_않는다()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("down")));
        var cache = Create(handler);

        await cache.GetAsync("T", "A");
        await cache.GetAsync("T", "A"); // 여기서 임계값(2회) 도달 → 회로 오픈
        var callsAfterOpen = handler.Calls;
        await cache.GetAsync("T", "A");
        await cache.GetAsync("T", "A");

        Assert.Equal(2, callsAfterOpen);
        Assert.Equal(callsAfterOpen, handler.Calls); // 회로가 열린 뒤에는 아예 나가지 않는다
    }

    [Fact]
    public async Task Set_은_사용자_편집본을_origin_user로_올린다()
    {
        string? body = null;
        var handler = new StubHandler(async req =>
        {
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var cache = Create(handler);

        var lyrics = Lyrics.Parse(Lrc)!;
        lyrics.Metadata.ServiceName = HttpRemoteLyricsCache.EditedServiceName;
        await cache.SetAsync("T", "A", lyrics);

        Assert.NotNull(body);
        Assert.Contains("\"origin\":\"user\"", body);
    }

    [Fact]
    public async Task Set_실패는_조용히_무시된다()
    {
        var cache = Create(new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("down"))));
        await cache.SetAsync("T", "A", Lyrics.Parse(Lrc)!); // 예외가 새어 나오면 실패
    }

    private static HttpRemoteLyricsCache Create(StubHandler handler) =>
        new("http://localhost:9/", "token", timeoutMs: 500, log: null, handler: handler);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>요청을 가로채는 스텁. 호출 횟수를 세어 서킷 브레이커를 검증한다.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return await responder(request);
        }
    }
}
