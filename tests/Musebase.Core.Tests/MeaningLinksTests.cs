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
}
