using System.Runtime.CompilerServices;
using Musebase.Core;
using Musebase.Core.Search;
using Xunit;

namespace Musebase.Core.Tests;

public class SearchTermCleanerTests
{
    [Theory]
    [InlineData("Shape of You (feat. Foo)", "Shape of You")]
    [InlineData("Song feat. Bar", "Song")]
    [InlineData("Bohemian Rhapsody - Remastered 2011", "Bohemian Rhapsody")]
    [InlineData("Song (Live)", "Song")]
    [InlineData("Track (Acoustic Version)", "Track")]
    [InlineData("Hit - Radio Edit", "Hit")]
    [InlineData("Song (Remastered) - Live at Wembley", "Song")]
    public void CleanTitle_RemovesNoise(string input, string expected)
    {
        Assert.Equal(expected, SearchTermCleaner.CleanTitle(input));
    }

    [Theory]
    [InlineData("Spider-Man Theme")]   // 공백 없는 대시는 보존
    [InlineData("Clean Song")]
    [InlineData("Song Part 2")]
    public void CleanTitle_PreservesCleanTitles(string input)
    {
        Assert.Equal(input, SearchTermCleaner.CleanTitle(input));
    }

    [Fact]
    public void CleanArtist_RemovesFeaturingOnly()
    {
        Assert.Equal("Alpha", SearchTermCleaner.CleanArtist("Alpha feat. Beta"));
        // 다인 아티스트(&, 콤마)는 보존 — Simon & Garfunkel 오손 방지
        Assert.Equal("Simon & Garfunkel", SearchTermCleaner.CleanArtist("Simon & Garfunkel"));
    }

    [Fact]
    public void Variants_ReturnsCleanedInfoVariant()
    {
        var variants = SearchTermCleaner.Variants(new SearchTerm("Song (Live)", "Artist"));
        Assert.Single(variants);
        Assert.Equal("Song", variants[0].Title);
        Assert.Equal("Artist", variants[0].Artist);
    }

    [Fact]
    public void Variants_EmptyWhenAlreadyClean()
    {
        Assert.Empty(SearchTermCleaner.Variants(new SearchTerm("Clean Song", "Artist")));
    }

    [Fact]
    public void Variants_CleansKeyword()
    {
        var variants = SearchTermCleaner.Variants(new SearchTerm("Song (Remastered)"));
        Assert.Single(variants);
        Assert.Equal("Song", variants[0].Keyword);
    }
}

public class SearchServiceExpansionTests
{
    private sealed class FakeProvider : ILyricsProvider
    {
        public string ServiceName { get; }
        public List<string> SeenTerms { get; } = new();
        private readonly Func<LyricsSearchRequest, Lyrics> _factory;

        public FakeProvider(string name, Func<LyricsSearchRequest, Lyrics> factory)
        {
            ServiceName = name;
            _factory = factory;
        }

        public async IAsyncEnumerable<Lyrics> GetLyricsAsync(
            LyricsSearchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            SeenTerms.Add(request.Term.ToString());
            await Task.Yield();
            var lyrics = _factory(request);
            lyrics.Metadata.ServiceName = ServiceName;
            yield return lyrics;
        }
    }

    private static Lyrics MakeLyrics(string token)
    {
        var lyrics = new Lyrics(new[] { new LyricsLine("la la", 0) });
        lyrics.IdTags[Lyrics.TagTitle] = "Song";
        lyrics.Metadata.ServiceToken = token;
        return lyrics;
    }

    [Fact]
    public async Task SearchAsync_SearchesOriginalAndCleanedVariant()
    {
        var provider = new FakeProvider("Fake", req => MakeLyrics(req.Term.ToString()));
        var service = new LyricsSearchService(provider);

        var results = await service.SearchAllAsync(LyricsSearchRequest.ByInfo("Song (Live)", "Artist"));

        Assert.Contains("Song (Live) Artist", provider.SeenTerms);
        Assert.Contains("Song Artist", provider.SeenTerms); // 정제 변형
        Assert.Equal(2, results.Count);                     // 서로 다른 토큰 → 둘 다 유지
    }

    [Fact]
    public async Task SearchAsync_DedupesSameSongAcrossVariants()
    {
        // 검색어와 무관하게 같은 곡(토큰) 반환 → 중복 제거로 1건
        var provider = new FakeProvider("Fake", _ => MakeLyrics("same-song"));
        var service = new LyricsSearchService(provider);

        var results = await service.SearchAllAsync(LyricsSearchRequest.ByInfo("Song (Remastered)", "A"));

        Assert.Equal(2, provider.SeenTerms.Count); // 원본 + 정제 변형 둘 다 검색됨
        Assert.Single(results);                     // 그러나 결과는 중복 제거
    }
}
