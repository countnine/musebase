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
