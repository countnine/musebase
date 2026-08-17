using System.Net;
using System.Text;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// Last.fm **계정** API(좋아요 읽기·쓰기). 두 가지가 계속 문제를 일으키는 지점이다 —
/// ① 오류가 HTTP 200에 담겨 온다 ② 쓰기는 서명이 틀리면 조용히 거절된다.
/// </summary>
public class LastFmAccountTests
{
    /// <summary>
    /// 문서의 예시를 그대로 고정한다: 이름순으로 &lt;이름&gt;&lt;값&gt;을 이어붙이고 secret을 붙여 MD5.
    /// 순서를 넣은 순서로 착각하면 서명이 통과하다가 파라미터를 하나 더한 날 갑자기 깨진다.
    /// </summary>
    [Fact]
    public void 서명은_이름순으로_이어붙여_해시한다()
    {
        var parameters = new Dictionary<string, string>
        {
            ["token"] = "xxx",          // 일부러 이름순과 다른 순서로 넣는다
            ["method"] = "auth.getSession",
            ["api_key"] = "abc",
        };

        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes("api_keyabcmethodauth.getSessiontokenxxxsecret"))).ToLowerInvariant();

        Assert.Equal(expected, LastFmAccount.Signature(parameters, "secret"));
    }

    [Fact]
    public async Task 좋아요_여부와_정식_주소를_읽는다()
    {
        var account = Create(_ => Json("""
            {"track":{"name":"Kids","url":"https://www.last.fm/music/MGMT/_/Kids","userloved":"1"}}
            """));

        var state = await account.GetStateAsync("Kids", "MGMT", "jay");

        Assert.NotNull(state);
        Assert.True(state!.Loved);
        Assert.Equal("https://www.last.fm/music/MGMT/_/Kids", state.Url);
    }

    [Fact]
    public async Task 좋아요하지_않은_곡은_false다()
    {
        var account = Create(_ => Json("""{"track":{"name":"Kids","userloved":"0"}}"""));
        var state = await account.GetStateAsync("Kids", "MGMT", "jay");
        Assert.False(state!.Loved);
    }

    /// <summary>
    /// Last.fm은 오류도 200에 담아 보낸다. 이것을 못 걸러 내면 "좋아요 안 함"으로 그려지고,
    /// 사람이 눌러서 <b>이미 켜 둔 좋아요를 끄게</b> 된다.
    /// </summary>
    [Fact]
    public async Task 오류가_200에_담겨_와도_실패로_본다()
    {
        var account = Create(_ => Json("""{"error":6,"message":"Track not found"}"""));
        Assert.Null(await account.GetStateAsync("Kids", "MGMT", "jay"));
    }

    [Fact]
    public async Task 서버가_죽어도_예외_대신_null()
    {
        var account = Create(_ => throw new HttpRequestException("down"));
        Assert.Null(await account.GetStateAsync("Kids", "MGMT", "jay"));
    }

    [Fact]
    public async Task 아이디를_모르면_아예_묻지_않는다()
    {
        var calls = 0;
        var account = Create(_ => { calls++; return Json("{}"); });

        Assert.Null(await account.GetStateAsync("Kids", "MGMT", ""));
        Assert.Equal(0, calls);
    }

    // ---- 쓰기 ----

    [Fact]
    public async Task 좋아요는_POST로_서명과_세션_키를_함께_보낸다()
    {
        string? body = null;
        HttpMethod? method = null;
        var account = Create(req =>
        {
            method = req.Method;
            body = req.Content?.ReadAsStringAsync().Result;
            return Json("{}");
        });

        Assert.True(await account.SetLovedAsync("Kids", "MGMT", loved: true, "session-key"));
        Assert.Equal(HttpMethod.Post, method);
        Assert.Contains("method=track.love", body);
        Assert.Contains("sk=session-key", body);
        Assert.Contains("api_sig=", body);
    }

    [Fact]
    public async Task 좋아요_해제는_track_unlove다()
    {
        string? body = null;
        var account = Create(req => { body = req.Content?.ReadAsStringAsync().Result; return Json("{}"); });

        await account.SetLovedAsync("Kids", "MGMT", loved: false, "session-key");
        Assert.Contains("method=track.unlove", body);
    }

    [Fact]
    public async Task 세션_키가_없으면_아무_요청도_보내지_않는다()
    {
        var calls = 0;
        var account = Create(_ => { calls++; return Json("{}"); });

        Assert.False(await account.SetLovedAsync("Kids", "MGMT", true, ""));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task secret이_없으면_연결도_쓰기도_못_한다()
    {
        var readOnly = new LastFmAccount("key", null);

        Assert.True(readOnly.CanRead);
        Assert.False(readOnly.CanConnect);
        Assert.False(await readOnly.SetLovedAsync("Kids", "MGMT", true, "sk"));
    }

    // ---- 승인 플로우 ----

    [Fact]
    public void 승인_주소에_돌아올_곳을_실어_보낸다()
    {
        // cb를 넘길 수 있어 API 계정에 콜백을 미리 등록하지 않아도 된다.
        var url = new LastFmAccount("api-key", "secret").AuthorizeUrl("https://box.ts.net/admin/lastfm/callback");

        Assert.StartsWith("https://www.last.fm/api/auth/?api_key=api-key&cb=", url);
        Assert.Contains("https%3A%2F%2Fbox.ts.net%2Fadmin%2Flastfm%2Fcallback", url);
    }

    [Fact]
    public async Task 토큰을_세션_키와_아이디로_바꾼다()
    {
        var account = Create(_ => Json("""{"session":{"name":"jay","key":"sk-123","subscriber":0}}"""));

        var session = await account.ExchangeTokenAsync("token");

        Assert.Equal("sk-123", session!.Value.Session);
        Assert.Equal("jay", session.Value.User);
    }

    [Fact]
    public async Task 이미_쓴_토큰은_실패로_돌아온다()
    {
        // 토큰은 1회용이라 두 번째부터 error 14가 온다 — HTTP는 여전히 200이다.
        var account = Create(_ => Json("""{"error":14,"message":"This token has not been authorized"}"""));
        Assert.Null(await account.ExchangeTokenAsync("token"));
    }

    private static LastFmAccount Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new("api-key", "secret", new HttpClient(new StubHandler(responder)), timeoutMs: 500);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }
}
