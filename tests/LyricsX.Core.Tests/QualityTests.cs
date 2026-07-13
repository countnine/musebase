using LyricsX.Core;
using LyricsX.Core.Search;
using Xunit;

namespace LyricsX.Core.Tests;

public class QualityTests
{
    private static Lyrics Make(string title, string artist, LyricsSearchRequest request,
        double? length = null, bool translated = false)
    {
        var line = new LyricsLine("content", 1.0);
        if (translated) line.Attachments[LineAttachments.TranslationTag()] = "번역";
        var lyrics = new Lyrics([line]);
        lyrics.IdTags[Lyrics.TagTitle] = title;
        lyrics.IdTags[Lyrics.TagArtist] = artist;
        if (length is { } len) lyrics.IdTags[Lyrics.TagLength] = len.ToString("0.##");
        lyrics.Metadata.Request = request;
        return lyrics;
    }

    private static readonly LyricsSearchRequest Request =
        LyricsSearchRequest.ByInfo("夜に駆ける", "YOASOBI", duration: 261);

    [Fact]
    public void Quality_ExactMatchScoresHigherThanMismatch()
    {
        var exact = Make("夜に駆ける", "YOASOBI", Request, length: 261);
        var wrongArtist = Make("夜に駆ける", "Someone Else", Request, length: 261);
        var wrongTitle = Make("전혀 다른 곡", "YOASOBI", Request, length: 261);

        Assert.True(exact.Quality() > wrongArtist.Quality());
        Assert.True(exact.Quality() > wrongTitle.Quality());
        Assert.True(exact.Quality() > 0.9);
    }

    [Fact]
    public void Quality_TranslationAddsBonus()
    {
        var plain = Make("夜に駆ける", "YOASOBI", Request, length: 261);
        var translated = Make("夜に駆ける", "YOASOBI", Request, length: 261, translated: true);

        Assert.Equal(plain.Quality() + 0.05, translated.Quality(), 3);
    }

    [Fact]
    public void Quality_DurationMismatchLowersScore()
    {
        var close = Make("夜に駆ける", "YOASOBI", Request, length: 262);
        var far = Make("夜に駆ける", "YOASOBI", Request, length: 300);

        Assert.True(close.Quality() > far.Quality());
    }

    [Fact]
    public void Quality_InstrumentalVariantPenalized()
    {
        var vocal = Make("夜に駆ける", "YOASOBI", Request, length: 261);
        var inst = Make("夜に駆ける (Instrumental)", "YOASOBI", Request, length: 261);

        Assert.True(vocal.Quality() > inst.Quality() + 0.2);
    }

    [Fact]
    public void Quality_InstrumentalNotPenalizedWhenRequested()
    {
        var instRequest = LyricsSearchRequest.ByInfo("夜に駆ける instrumental", "YOASOBI");
        var inst = Make("夜に駆ける (Instrumental)", "YOASOBI", instRequest);

        // 검색어 자체가 반주 버전이면 페널티 없음
        Assert.True(inst.Quality() > 0.5);
    }

    [Fact]
    public void Quality_IsCachedInMetadata()
    {
        var lyrics = Make("t", "a", Request);
        var first = lyrics.Quality();
        lyrics.IdTags[Lyrics.TagTitle] = "변경해도 캐시 유지";
        Assert.Equal(first, lyrics.Quality());
    }

    [Fact]
    public void IsMatched_SubstringTitleCounts()
    {
        var live = Make("夜に駆ける (Live)", "YOASOBI", Request);
        Assert.True(live.IsMatched());

        var unrelated = Make("완전 다른 곡", "다른 가수", Request);
        Assert.False(unrelated.IsMatched());
    }

    [Fact]
    public void EditDistance_MatchesClassicCases()
    {
        Assert.Equal(0, LyricsQuality.EditDistance("abc", "abc"));
        Assert.Equal(1, LyricsQuality.EditDistance("abc", "abd"));
        Assert.Equal(3, LyricsQuality.EditDistance("kitten", "sitting"));
    }
}
