using Musebase.Core.Search;
using Musebase.Core.Translation;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>가사 소스·번역 엔진 레지스트리의 조합·메타데이터 계약(네트워크 없음).</summary>
public class RegistryTests
{
    [Fact]
    public void LyricsSources_OnlyLrclibIsOfficial()
    {
        Assert.Contains(LyricsSourceRegistry.All, d => d.Id == "lrclib" && d.IsOfficialApi);
        Assert.Equal(new[] { "lrclib" }, LyricsSourceRegistry.OfficialIds);
        Assert.Contains("netease", LyricsSourceRegistry.AllIds);
        Assert.All(
            LyricsSourceRegistry.All.Where(d => d.Id != "lrclib"),
            d => Assert.False(d.IsOfficialApi));
    }

    [Fact]
    public void LyricsSourceBuild_SelectsEnabledOnly_InRegistryOrder()
    {
        var providers = LyricsSourceRegistry.Build(["qqmusic", "lrclib"]);
        // 등록 순서(lrclib 먼저)로 생성, 활성만 포함
        Assert.Equal(2, providers.Length);
        Assert.Equal("LRCLIB", providers[0].ServiceName);
        Assert.Equal("QQMusic", providers[1].ServiceName);
    }

    [Fact]
    public void LyricsSourceBuild_UnknownIdIgnored()
    {
        Assert.Empty(LyricsSourceRegistry.Build(["does-not-exist"]));
    }

    [Fact]
    public void Translator_LibreIsKeylessFree_DeeplRequiresKey()
    {
        var libre = TranslatorRegistry.Find("libretranslate");
        Assert.NotNull(libre);
        Assert.False(libre!.RequiresApiKey);
        Assert.True(libre.IsFree);

        var deepl = TranslatorRegistry.Find("deepl");
        Assert.NotNull(deepl);
        Assert.True(deepl!.RequiresApiKey);
    }

    [Fact]
    public void TranslatorBuild_LibreWithoutKey_Builds_DeeplWithoutKey_IsNull()
    {
        Assert.NotNull(TranslatorRegistry.Build("libretranslate", new TranslatorOptions()));
        Assert.Null(TranslatorRegistry.Build("deepl", new TranslatorOptions()));
        Assert.NotNull(TranslatorRegistry.Build("deepl", new TranslatorOptions(DeeplApiKey: "key:fx")));
    }

    [Fact]
    public void TranslatorBuild_NoneOrEmpty_IsNull()
    {
        Assert.Null(TranslatorRegistry.Build("none", new TranslatorOptions()));
        Assert.Null(TranslatorRegistry.Build("", new TranslatorOptions()));
    }
}
