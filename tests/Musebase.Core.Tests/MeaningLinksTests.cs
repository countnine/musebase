using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 곡 배경·의미를 읽으러 가는 외부 링크 조립. 관리자 화면에 그대로 박히므로
/// 이스케이프가 틀리면 링크가 깨지거나 HTML이 샌다.
/// </summary>
public class MeaningLinksTests
{
    [Fact]
    public void 검색어는_아티스트_다음에_제목이다()
    {
        Assert.Equal("MGMT Kids", MeaningLinks.Query("Kids", "MGMT"));
    }

    [Fact]
    public void 아티스트가_없으면_제목만_쓴다()
    {
        Assert.Equal("Kids", MeaningLinks.Query("Kids", ""));
        Assert.Equal("Kids", MeaningLinks.Query("Kids", "   "));
    }

    [Fact]
    public void 공백과_특수문자는_URL로_이스케이프된다()
    {
        var url = MeaningLinks.MusixmatchSearch("Don't Delete The Kisses", "Wolf Alice");
        // 경로형(/search/{검색어})은 실측에서 403이다 — 쿼리 형식이어야 한다.
        Assert.StartsWith("https://www.musixmatch.com/search?query=", url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("%20", url);
        Assert.Contains("%27", url); // 작은따옴표
    }

    [Fact]
    public void 한글_제목도_깨지지_않는다()
    {
        var url = MeaningLinks.GeniusSearch("우리 그럼 앞으로", "Kim Mok In");
        Assert.StartsWith("https://genius.com/search?q=", url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("%EC%9A%B0", url); // "우"
    }

    /// <summary>
    /// Spotify가 붙이는 꼬리표가 그대로 들어와도 링크 자체는 유효해야 한다
    /// (검색 품질은 소스 수집 단계에서 SearchTermCleaner가 다룬다).
    /// </summary>
    [Fact]
    public void 불릿_꼬리표가_붙어도_링크가_깨지지_않는다()
    {
        var url = MeaningLinks.GeniusSearch("Go!", "M83 • 스마트셔플 추천");
        Assert.StartsWith("https://genius.com/search?q=", url);
        Assert.DoesNotContain(" ", url);
    }

    [Fact]
    public void 정확한_Genius_주소를_알면_검색_대신_그것을_쓴다()
    {
        var known = "https://genius.com/Mgmt-kids-lyrics";
        Assert.Equal(known, MeaningLinks.Genius("Kids", "MGMT", known));
        Assert.StartsWith("https://genius.com/search?q=", MeaningLinks.Genius("Kids", "MGMT", null));
        Assert.StartsWith("https://genius.com/search?q=", MeaningLinks.Genius("Kids", "MGMT", "  "));
    }

    /// <summary>
    /// 검색으로 흔히 나오는 <c>/search/site?q=</c>는 실측에서 404다 — 그쪽으로 보내면
    /// 사람이 "Tunefind가 죽었나" 하고 만다.
    /// </summary>
    [Fact]
    public void Tunefind는_경로형이_아니라_query다()
    {
        var url = MeaningLinks.Tunefind("Kids", "MGMT");

        Assert.StartsWith("https://www.tunefind.com/search?q=", url);
        Assert.DoesNotContain("/search/site", url);
        Assert.Contains("MGMT%20Kids", url);
    }

    [Fact]
    public void YouTube는_아티스트와_제목으로_검색한다()
    {
        var url = MeaningLinks.YouTube("Kids", "MGMT");

        Assert.StartsWith("https://www.youtube.com/results?search_query=", url);
        Assert.Contains("MGMT%20Kids", url);
    }

    [Fact]
    public void LastFm은_확인한_주소를_우선한다()
    {
        var known = "https://www.last.fm/music/MGMT/_/Kids";
        Assert.Equal(known, MeaningLinks.LastFm("Kids", "MGMT", known));
    }

    /// <summary>
    /// Musixmatch와 달리 규칙 생성이 허용된다 — 이름이 안 맞으면 <b>다른 곡으로 넘어가지 않고</b>
    /// "그런 곡 없음"이 뜬다. 대신 조각마다 이스케이프해서 슬래시가 경로를 늘리지 않게 한다.
    /// </summary>
    [Fact]
    public void LastFm_주소를_모르면_이름으로_만든다()
    {
        Assert.Equal("https://www.last.fm/music/MGMT/_/Kids", MeaningLinks.LastFm("Kids", "MGMT", null));

        var slashed = MeaningLinks.LastFm("Shallow", "Lady Gaga/Bradley Cooper", null);
        Assert.Equal("https://www.last.fm/music/Lady%20Gaga%2FBradley%20Cooper/_/Shallow", slashed);
    }

    [Fact]
    public void 아티스트를_모르면_LastFm_검색으로_보낸다()
    {
        // 이름 없이 /music//_/Kids를 만들면 404가 아니라 엉뚱한 페이지가 된다.
        Assert.StartsWith("https://www.last.fm/search?q=", MeaningLinks.LastFm("Kids", "", null));
    }
}
