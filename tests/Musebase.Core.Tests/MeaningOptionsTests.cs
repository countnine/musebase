using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 의미 자료원 선택. Musixmatch 자료는 사람이 쓴 해설이 아니라 기계가 가사를 분석한 결과라
/// **기본으로 켜지지 않아야 한다** — 켤지 말지는 운영자가 정한다.
/// </summary>
public class MeaningOptionsTests
{
    [Fact]
    public void 기본_소스에_musixmatch는_없다()
    {
        var sources = MeaningOptions.ParseSources(null, null);

        Assert.Equal(["genius", "lastfm", "wikipedia"], sources);
        Assert.DoesNotContain("musixmatch", sources);
    }

    [Fact]
    public void 설정한_소스만_구성된다()
    {
        Assert.Equal(["wikipedia"], MeaningOptions.ParseSources("wikipedia", null));
        Assert.Equal(["genius", "musixmatch"], MeaningOptions.ParseSources("genius, musixmatch", null));
        Assert.Equal(["genius"], MeaningOptions.ParseSources("GENIUS", null)); // 대소문자 무시
    }

    [Fact]
    public void 모르는_이름은_무시한다()
    {
        // 오타 하나로 서버가 죽으면 안 된다 — 조용히 빼고 나머지로 돌린다.
        Assert.Equal(["genius"], MeaningOptions.ParseSources("genius,geniuss,songfacts", null));
        Assert.Empty(MeaningOptions.ParseSources("nonsense", null));
    }

    [Fact]
    public void 예전_위키피디아_스위치를_계속_받아_준다()
    {
        // 소스 목록이 생기기 전부터 쓰던 변수라, 목록을 직접 지정하지 않은 경우에만 적용한다.
        Assert.Equal(["genius", "lastfm"], MeaningOptions.ParseSources(null, "0"));

        // 직접 지정이 항상 이긴다.
        Assert.Equal(["wikipedia"], MeaningOptions.ParseSources("wikipedia", "0"));
    }

    [Fact]
    public void 키가_없는_소스는_구성에서_빠진다()
    {
        var options = Empty with { Sources = ["genius", "lastfm", "wikipedia", "musixmatch"] };

        // 위키피디아만 키가 필요 없다.
        Assert.Equal(["Wikipedia"], options.BuildService().SourceNames);
    }

    [Fact]
    public void musixmatch는_고르고_키가_있을_때만_붙는다()
    {
        var keyed = Empty with { MusixmatchKey = "k", Sources = ["musixmatch"] };
        Assert.Single(keyed.BuildService().SourceNames);
        Assert.Contains("AI 분석", keyed.BuildService().SourceNames[0]); // 출처에 성격이 드러난다

        var notChosen = Empty with { MusixmatchKey = "k", Sources = ["wikipedia"] };
        Assert.DoesNotContain(notChosen.BuildService().SourceNames, n => n.Contains("Musixmatch"));
    }

    private static readonly MeaningOptions Empty = new(
        Engine: "none", Lang: "ko",
        GeminiApiKey: null, GeminiModel: null, OpenRouterApiKey: null, OpenRouterModel: null,
        GeniusToken: null, LastFmKey: null, LastFmSecret: null, MusixmatchKey: null,
        Sources: [], BackfillLimit: 50, BackfillDelayMs: 0);
}
