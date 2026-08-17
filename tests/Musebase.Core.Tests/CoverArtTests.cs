using System.Net;
using System.Text;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 커버 이미지 찾기. 검색 API는 <b>무엇을 넣든 뭔가를 돌려주므로</b> 관련성 검사가 핵심이다 —
/// 엉뚱한 표지가 붙으면 곡을 잘못 알아본다.
/// </summary>
public class CoverArtTests
{
    private const string ITunesHit = """
        {"resultCount":1,"results":[{"trackName":"Kids","artistName":"MGMT",
         "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/a/b.jpg/100x100bb.jpg"}]}
        """;

    [Fact]
    public async Task iTunes_아트워크를_600으로_승격한다()
    {
        var cover = await Create(_ => Json(ITunesHit)).FindAsync("Kids", "MGMT");

        Assert.NotNull(cover);
        Assert.Equal(CoverArt.ITunes, cover!.Source);
        Assert.EndsWith("/600x600bb.jpg", cover.Url);
    }

    [Fact]
    public void 예상과_다른_주소는_건드리지_않는다()
    {
        // 억지로 크기를 바꾸면 404가 된다 — 모르는 형태는 그대로 쓴다.
        var url = "https://is1-ssl.mzstatic.com/image/thumb/a/b.jpg/60x60bb.png";
        Assert.Equal(url, CoverArt.Promote(url));
    }

    /// <summary>Genius·Musixmatch에서 이미 겪은 함정 — 첫 결과를 그냥 믿으면 안 된다.</summary>
    [Fact]
    public async Task 무관한_곡의_표지는_쓰지_않는다()
    {
        var unrelated = """
            {"results":[{"trackName":"119 REMIX","artistName":"Someone Else",
             "artworkUrl100":"https://is1-ssl.mzstatic.com/x/100x100bb.jpg"}]}
            """;

        var cover = await Create(req => req.RequestUri!.Host.Contains("itunes")
            ? Json(unrelated)
            : Json("""{"data":[]}""")).FindAsync("Kids", "MGMT");

        Assert.Null(cover);
    }

    [Fact]
    public async Task iTunes가_비면_Deezer로_간다()
    {
        var deezer = """
            {"data":[{"title":"Kids","artist":{"name":"MGMT"},
             "album":{"cover_big":"https://cdn-images.dzcdn.net/images/cover/x/500x500.jpg"}}]}
            """;

        var cover = await Create(req => req.RequestUri!.Host.Contains("itunes")
            ? Json("""{"resultCount":0,"results":[]}""")
            : Json(deezer)).FindAsync("Kids", "MGMT");

        Assert.Equal(CoverArt.Deezer, cover!.Source);
        Assert.StartsWith("https://cdn-images.dzcdn.net/", cover.Url);
    }

    [Fact]
    public async Task 둘_다_없으면_null이고_예외는_없다()
    {
        var cover = await Create(_ => throw new HttpRequestException("down")).FindAsync("Kids", "MGMT");
        Assert.Null(cover);
    }

    [Fact]
    public async Task 응답이_깨져도_null()
    {
        Assert.Null(await Create(_ => Json("{ this is not json")).FindAsync("Kids", "MGMT"));
    }

    private static CoverArt Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)), timeoutMs: 500);

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
