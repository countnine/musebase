using Musebase.Core.Translation;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>"번역돼 있다"를 태그 유무가 아니라 채워진 정도로 본다.</summary>
public class TranslationCoverageTests
{
    private static Lyrics Parse(string lrc) => Lyrics.Parse(lrc)!;

    [Fact]
    public void 크레딧_한_줄만_번역된_곡은_번역되지_않은_것이다()
    {
        var lyrics = Parse("""
            [00:01.00]Produced by：James Ford
            [00:01.00][tr:ko]제작: 제임스 포드
            [00:02.00]I wanna see you alone
            [00:02.00][tr]我想独自见你
            [00:03.00]I wanna sharp the stone
            """);

        Assert.Equal((3, 1), TranslationCoverage.Count(lyrics, "KO"));
        Assert.False(TranslationCoverage.IsTranslated(lyrics, "ko"));
    }

    [Fact]
    public void 대부분_번역됐으면_번역된_것이다()
    {
        var lines = Enumerable.Range(1, 10)
            .Select(i => $"[00:{i:00}.00]line {i}\n" + (i <= 8 ? $"[00:{i:00}.00][tr:ko]줄 {i}\n" : ""));
        Assert.True(TranslationCoverage.IsTranslated(Parse(string.Concat(lines)), "ko"));
    }

    [Fact]
    public void 한국_노래의_한글_줄과_글자_없는_줄은_세지_않는다()
    {
        var lyrics = Parse("""
            [00:01.00]나 오늘 어제보다 어두워도
            [00:02.00]♪
            [00:03.00]词：HEN
            [00:03.00][tr:ko]작사: HEN
            [00:04.00]어제보다 힘들어도
            """);

        Assert.Equal((1, 1), TranslationCoverage.Count(lyrics, "ko"));
        Assert.True(TranslationCoverage.IsTranslated(lyrics, "ko"));
    }

    [Fact]
    public void 번역이_필요한_줄이_없으면_번역된_것이다()
    {
        Assert.True(TranslationCoverage.IsTranslated(Parse("[00:01.00]안녕하세요\n"), "ko"));
    }
}
