using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 가사 서버의 PUT 병합 정책(`contracts/lyrics-api.md`). 핵심은 두 가지 퇴화를 막는 것:
/// ① 사용자가 손으로 고친 가사를 다른 기기의 자동 검색이 덮어쓰는 것,
/// ② 글자 단위 카라오케·번역이 붙은 가사가 빈약한 가사로 대체되는 것.
/// </summary>
public class MergePolicyTests
{
    private const string ProviderLrc = """
        [00:01.00]line one
        [00:02.00]line two
        [00:03.00]line three
        """;

    [Fact]
    public void 새_곡은_그대로_저장된다()
    {
        var decision = MergePolicy.Evaluate(null, "provider", LyricsFacts.From(ProviderLrc));
        Assert.Equal(MergePolicy.Decision.Accept, decision);
    }

    [Fact]
    public void 사용자_편집본은_자동_검색이_덮어쓰지_못한다()
    {
        var existing = ("user", new LyricsFacts(3, false, ["ko"]));
        var decision = MergePolicy.Evaluate(existing, "provider", LyricsFacts.From(ProviderLrc));
        Assert.Equal(MergePolicy.Decision.RejectUserEditProtected, decision);
    }

    [Fact]
    public void 사용자_편집본은_언제나_채택된다()
    {
        var existing = ("provider", new LyricsFacts(50, true, ["ko", "ja"]));
        var decision = MergePolicy.Evaluate(existing, "user", new LyricsFacts(2, false, []));
        Assert.Equal(MergePolicy.Decision.Accept, decision);
    }

    [Fact]
    public void 카라오케_태그가_사라지는_갱신은_거부된다()
    {
        var existing = ("provider", new LyricsFacts(30, HasInlineTimeTags: true, ["ko"]));
        var decision = MergePolicy.Evaluate(existing, "provider", new LyricsFacts(30, false, ["ko"]));
        Assert.Equal(MergePolicy.Decision.RejectPoorerContent, decision);
    }

    [Fact]
    public void 번역이_늘어난_갱신은_채택된다()
    {
        var existing = ("provider", new LyricsFacts(30, false, ["ko"]));
        var decision = MergePolicy.Evaluate(existing, "provider", new LyricsFacts(30, false, ["ko", "ja"]));
        Assert.Equal(MergePolicy.Decision.Accept, decision);
    }

    [Fact]
    public void 줄_수가_비슷하면_사소한_차이는_거부하지_않는다()
    {
        var existing = ("provider", new LyricsFacts(30, false, ["ko"]));
        var decision = MergePolicy.Evaluate(existing, "provider", new LyricsFacts(28, false, ["ko"]));
        Assert.Equal(MergePolicy.Decision.Accept, decision);
    }

    [Fact]
    public void LRC에서_줄수_번역언어_카라오케를_뽑아낸다()
    {
        var lrc = """
            [00:01.00]hello
            [00:01.00][tr:ko]안녕
            [00:01.00][tt]<0,0><300,5>
            [00:02.00]world
            """;

        var facts = LyricsFacts.From(lrc);

        Assert.Equal(2, facts.LineCount);
        Assert.True(facts.HasInlineTimeTags);
        Assert.Equal(["ko"], facts.Langs);
    }

    [Fact]
    public void 파싱할_수_없는_LRC는_빈_사실이_된다()
    {
        Assert.Equal(LyricsFacts.Empty, LyricsFacts.From("가사가 아님"));
    }

    // ---- 키 정규화 ----

    [Fact]
    public void 정확_키는_클라이언트_로컬_캐시_키와_같다()
    {
        Assert.Equal(
            Musebase.Core.Search.LyricsCacheStore.MakeKey("Electric Feel", "MGMT"),
            LyricsStore.ExactKey("Electric Feel", "MGMT"));
    }

    [Theory]
    // Windows SMTC는 플레이어에 따라 아티스트에 앨범을 붙여 보고한다 — 그 꼬리를 떼어야
    // 아티스트만 보고하는 Android와 같은 곡으로 맞는다.
    [InlineData("MGMT — Oracular Spectacular", "MGMT")]
    [InlineData("Holly Humberstone – It's a Real Cruel World - EP", "Holly Humberstone")]
    // 이름 안의 붙임표는 건드리지 않는다.
    [InlineData("Jay-Z", "Jay-Z")]
    [InlineData("The Rolling Stones", "The Rolling Stones")]
    public void 아티스트의_앨범_꼬리만_떼어_낸다(string input, string expected)
    {
        Assert.Equal(expected, LyricsStore.StripAlbumSuffix(input));
    }

    [Fact]
    public void 아티스트만_보내도_앨범_붙은_저장본과_같은_느슨한_키가_된다()
    {
        var stored = LyricsStore.PrimaryLooseKey("Electric Feel", "MGMT — Oracular Spectacular");
        var queried = LyricsStore.ExactKey("Electric Feel", "MGMT");
        Assert.Equal(stored, queried);
    }

    [Fact]
    public void 제목의_잡음도_느슨한_키_후보에_들어간다()
    {
        var keys = LyricsStore.LooseKeys("Love Story (Taylor's Version)", "Taylor Swift");
        Assert.Contains(LyricsStore.ExactKey("Love Story", "Taylor Swift"), keys);
    }
}
