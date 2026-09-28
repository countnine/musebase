using System.Net;
using System.Text;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// Spotify 라이브러리 연동. 2026-02 정책 변경으로 <b>엔드포인트가 바뀌었고</b>(옛 <c>/me/tracks</c>는
/// 403) 검색 <c>limit</c> 상한도 내려갔다 — 실제로 그 주소를 부르는지, 검색 결과를 곧이곧대로
/// 믿지 않는지가 여기서 갈린다.
/// </summary>
public class SpotifyAccountTests
{
    [Fact]
    public void 자격증명이_없으면_연결_자체를_못_한다()
    {
        Assert.False(new SpotifyAccount(null, null).CanConnect);
        Assert.False(new SpotifyAccount("id", "").CanConnect);
        Assert.True(new SpotifyAccount("id", "secret").CanConnect);
    }

    [Fact]
    public void 승인_주소에_스코프와_콜백과_state를_싣는다()
    {
        var url = new SpotifyAccount("client-id", "secret")
            .AuthorizeUrl("https://box.ts.net/musebase/spotify/callback", "nonce-123");

        Assert.StartsWith("https://accounts.spotify.com/authorize?client_id=client-id", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=nonce-123", url);
        Assert.Contains(Uri.EscapeDataString("https://box.ts.net/musebase/spotify/callback"), url);

        // 라이브러리를 읽고 쓰려면 둘 다 필요하다.
        Assert.Contains(Uri.EscapeDataString("user-library-read user-library-modify"), url);
    }

    [Fact]
    public async Task 담을_때_새_엔드포인트를_부른다()
    {
        // 옛 PUT /v1/me/tracks는 2026-02에 폐기됐다 — 토큰이 맞아도 403이다.
        var calls = new List<(string Method, string Url)>();
        var account = Create(calls, Json("""{"access_token":"at","expires_in":3600}"""));

        await account.SetSavedAsync("spotify:track:abc", saved: true, refresh: "rt");

        var write = calls.Last();
        Assert.Equal("PUT", write.Method);
        Assert.StartsWith("https://api.spotify.com/v1/me/library?uris=", write.Url);
        Assert.Contains(Uri.EscapeDataString("spotify:track:abc"), write.Url);
        Assert.DoesNotContain("/me/tracks", write.Url);
    }

    [Fact]
    public async Task 뺄_때는_DELETE다()
    {
        var calls = new List<(string Method, string Url)>();
        var account = Create(calls, Json("""{"access_token":"at","expires_in":3600}"""));

        await account.SetSavedAsync("spotify:track:abc", saved: false, refresh: "rt");

        Assert.Equal("DELETE", calls.Last().Method);
        Assert.StartsWith("https://api.spotify.com/v1/me/library?uris=", calls.Last().Url);
    }

    [Fact]
    public async Task 트랙_URI가_없으면_아무것도_하지_않는다()
    {
        var calls = new List<(string Method, string Url)>();
        var account = Create(calls, Json("{}"));

        Assert.False(await account.SetSavedAsync("", saved: true, refresh: "rt"));
        Assert.Empty(calls);   // 토큰조차 받으러 가지 않는다
    }

    [Fact]
    public async Task 갱신_토큰이_없으면_조용히_실패한다()
    {
        var account = new SpotifyAccount("id", "secret");

        Assert.False(await account.SetSavedAsync("spotify:track:abc", true, refresh: ""));
        Assert.Null(await account.GetStateAsync("Kids", "MGMT", refresh: "", knownUri: null));
    }

    [Fact]
    public async Task 검색은_무관한_곡을_고르지_않는다()
    {
        // 검색은 리믹스·커버·노래방 음원을 곧잘 위로 올린다 — 첫 결과를 그대로 쓰면 엉뚱한 곡을 담는다.
        var body = Json("""
            {"tracks":{"items":[
              {"name":"Kids (Karaoke Version)","artists":[{"name":"Karaoke Crew"}],"uri":"spotify:track:wrong"},
              {"name":"Kids","artists":[{"name":"MGMT"}],"uri":"spotify:track:right"}
            ]}}
            """);
        var account = Create([], body);

        Assert.Equal("spotify:track:right", await account.FindUriAsync("Kids", "MGMT", "at"));
    }

    [Fact]
    public async Task 맞는_곡이_하나도_없으면_null이다()
    {
        var body = Json("""
            {"tracks":{"items":[{"name":"완전 다른 곡","artists":[{"name":"다른 사람"}],"uri":"spotify:track:x"}]}}
            """);
        Assert.Null(await Create([], body).FindUriAsync("Kids", "MGMT", "at"));
    }

    [Fact]
    public async Task 검색_상한은_10이다()
    {
        // 2026-02부터 limit 최대가 50 → 10으로 내려갔다. 넘기면 400이다.
        var calls = new List<(string Method, string Url)>();
        await Create(calls, Json("""{"tracks":{"items":[]}}""")).FindUriAsync("Kids", "MGMT", "at");

        Assert.Contains("limit=10", Assert.Single(calls).Url);
    }

    [Fact]
    public async Task 응답이_깨져도_예외가_아니라_null이다()
    {
        Assert.Null(await Create([], new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        }).FindUriAsync("Kids", "MGMT", "at"));

        Assert.Null(await Create([], new HttpResponseMessage(HttpStatusCode.Forbidden))
            .FindUriAsync("Kids", "MGMT", "at"));
    }

    [Fact]
    public async Task 같은_계정이면_토큰을_다시_받지_않는다()
    {
        var calls = new List<(string Method, string Url)>();
        var account = Create(calls, Json("""{"access_token":"at","expires_in":3600}"""));

        await account.SetSavedAsync("spotify:track:a", saved: true, refresh: "rt-1");
        await account.SetSavedAsync("spotify:track:b", saved: true, refresh: "rt-1");

        Assert.Equal(1, calls.Count(c => c.Url.Contains("accounts.spotify.com")));
    }

    [Fact]
    public async Task 계정을_바꾸면_옛_계정_토큰을_쓰지_않는다()
    {
        // 다시 연결하면 갱신 토큰이 바뀐다. 캐시가 이걸 안 봐서 최대 1시간 동안 좋아요가 옛 계정에 담겼다.
        var calls = new List<(string Method, string Url)>();
        var account = Create(calls, Json("""{"access_token":"at","expires_in":3600}"""));

        await account.SetSavedAsync("spotify:track:a", saved: true, refresh: "old-account");
        await account.SetSavedAsync("spotify:track:a", saved: true, refresh: "new-account");

        Assert.Equal(2, calls.Count(c => c.Url.Contains("accounts.spotify.com")));
    }

    // ---- 계정 연결(코드 교환) ----

    [Fact]
    public async Task 표시_이름을_못_읽어도_연결을_버리지_않는다()
    {
        // 갱신 토큰은 이 한 번만 받을 수 있다 — /me가 403이라고 통째로 버리면(Development Mode
        // 사용자 목록·Premium 문제) 다시 눌러도 같은 자리에서 또 실패해 영영 연결되지 않는다.
        var account = Routed(new List<string>(), url =>
            url.Contains("/me") ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json("""{"access_token":"at","refresh_token":"rt","expires_in":3600}"""));

        string? why = null;
        var session = await account.ExchangeCodeAsync("code", "https://box/cb", w => why = w);

        Assert.NotNull(session);
        Assert.Equal("rt", session!.Value.Refresh);
        Assert.Equal(SpotifyAccount.UnknownUser, session.Value.User);
        Assert.Contains("/me", why);      // 숨기지는 않는다
    }

    [Fact]
    public async Task 토큰_교환이_거절되면_사유를_그대로_알린다()
    {
        var account = Routed(new List<string>(), _ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":"invalid_grant","error_description":"Invalid redirect URI"}""",
                    Encoding.UTF8, "application/json"),
            });

        string? why = null;
        var session = await account.ExchangeCodeAsync("code", "https://box/cb", w => why = w);

        Assert.Null(session);
        // "토큰을 받지 못했습니다"만으로는 콜백 주소 문제인지 시크릿 문제인지 알 수 없다.
        Assert.Contains("HTTP 400", why);
        Assert.Contains("invalid_grant", why);
    }

    [Fact]
    public async Task 이름을_읽으면_그_이름으로_연결된다()
    {
        var account = Routed(new List<string>(), url =>
            url.Contains("/me") ? Json("""{"display_name":"제이","id":"jay"}""")
                : Json("""{"access_token":"at","refresh_token":"rt","expires_in":3600}"""));

        string? why = null;
        var session = await account.ExchangeCodeAsync("code", "https://box/cb", w => why = w);

        Assert.Equal("제이", session!.Value.User);
        Assert.Null(why);
    }

    // ---- 라이브러리 전량 조회 ----

    [Fact]
    public async Task 라이브러리를_쪽으로_나눠_끝까지_받는다()
    {
        var urls = new List<string>();
        var account = Routed(urls, url =>
            url.Contains("offset=0") ? Json(Page(50)) :
            url.Contains("offset=50") ? Json(Page(7)) :
            Json("""{"access_token":"at","expires_in":3600}"""));

        var saved = await account.GetSavedTracksAsync("rt");

        Assert.NotNull(saved);
        Assert.Equal(57, saved!.Count);
        // 마지막 쪽이 요청한 수보다 적게 왔으므로 세 번째 쪽은 부르지 않는다.
        Assert.DoesNotContain(urls, u => u.Contains("offset=100"));
        Assert.Equal("곡 0", saved[0].Title);
        Assert.Equal("spotify:track:0", saved[0].Uri);
        Assert.Equal("가수 A, 가수 B", saved[0].Artist);
    }

    [Fact]
    public async Task 도중에_실패하면_통째로_버리고_이유를_알린다()
    {
        // 부분만 저장하면 못 받은 곡이 "좋아요 해제"로 보인다(동기화가 먼저 전부 0으로 내린다).
        var account = Routed(new List<string>(), url =>
            url.Contains("offset=0") ? Json(Page(50)) :
            url.Contains("offset=50") ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
            Json("""{"access_token":"at","expires_in":3600}"""));

        string? why = null;
        var saved = await account.GetSavedTracksAsync("rt", onFailure: w => why = w);

        Assert.Null(saved);
        Assert.Equal("HTTP 403", why);   // 조용히 작은 limit으로 강등하지 않는다
    }

    [Fact]
    public async Task 라이브러리가_비어_있으면_빈_목록이다()
    {
        var account = Routed(new List<string>(), url =>
            url.Contains("/me/tracks") ? Json("""{"items":[],"total":0}""")
                                       : Json("""{"access_token":"at","expires_in":3600}"""));

        var saved = await account.GetSavedTracksAsync("rt");

        Assert.NotNull(saved);
        Assert.Empty(saved!);            // "없음"과 "못 받음"은 다르다
    }

    /// <summary>items가 <paramref name="count"/>개인 <c>/me/tracks</c> 응답.</summary>
    private static string Page(int count)
    {
        var items = Enumerable.Range(0, count).Select(i =>
            "{\"track\":{\"name\":\"곡 " + i + "\",\"uri\":\"spotify:track:" + i + "\","
            + "\"artists\":[{\"name\":\"가수 A\"},{\"name\":\"가수 B\"}]}}");
        return "{\"items\":[" + string.Join(",", items) + "],\"total\":" + count + "}";
    }

    private static SpotifyAccount Routed(List<string> urls, Func<string, HttpResponseMessage> respond) =>
        new("id", "secret", new HttpClient(new RouteHandler(urls, respond)));

    /// <summary>URL마다 다른 응답을 주는 스텁(페이징을 보려면 필요하다).</summary>
    private sealed class RouteHandler(List<string> urls, Func<string, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            urls.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    // ---- 도구 ----

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static SpotifyAccount Create(List<(string, string)> calls, HttpResponseMessage response) =>
        new("id", "secret", new HttpClient(new StubHandler(calls, response)));

    private sealed class StubHandler(List<(string, string)> calls, HttpResponseMessage response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Add((request.Method.Method, request.RequestUri!.ToString()));

            // 같은 응답을 여러 번 돌려줘야 하므로 매번 새로 만든다(HttpContent는 한 번만 읽힌다).
            var body = response.Content is null ? "" : response.Content.ReadAsStringAsync().Result;
            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
