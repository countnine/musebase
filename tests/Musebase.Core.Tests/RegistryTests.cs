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
    public void Translator_GoogleRequiresKey_LibreAcceptsOptionalKey()
    {
        var google = TranslatorRegistry.Find("google");
        Assert.NotNull(google);
        Assert.True(google!.RequiresApiKey);
        Assert.True(google.UsesApiKey);
        Assert.False(google.IsFree);
        Assert.Equal("Google Cloud Translation", google.Name); // "{engine} API 키" 문구용 짧은 이름

        var libre = TranslatorRegistry.Find("libretranslate")!;
        Assert.False(libre.RequiresApiKey);
        Assert.True(libre.AcceptsApiKey);   // 키 없이도 되지만 넣으면 사용
        Assert.True(libre.UsesApiKey);

        Assert.False(TranslatorRegistry.Find("mymemory")!.UsesApiKey);
    }

    [Fact]
    public void TranslatorBuild_GoogleWithoutKey_IsNull()
    {
        Assert.Null(TranslatorRegistry.Build("google", new TranslatorOptions()));
        Assert.NotNull(TranslatorRegistry.Build("google", new TranslatorOptions(GoogleApiKey: "AIza-test")));
    }

    [Theory]
    [InlineData("KO", "ko")]
    [InlineData("EN-US", "en")]
    [InlineData("PT-BR", "pt")]
    [InlineData("ZH", "zh-CN")]
    [InlineData("ZH-HANT", "zh-TW")]
    [InlineData("NB", "no")]
    [InlineData("", "en")]
    public void GoogleTranslator_MapsDeeplStyleTargetLang(string targetLang, string expected) =>
        Assert.Equal(expected, GoogleTranslateTranslator.ToGoogleLanguage(targetLang));

    [Fact]
    public void TranslatorBuild_NoneOrEmpty_IsNull()
    {
        Assert.Null(TranslatorRegistry.Build("none", new TranslatorOptions()));
        Assert.Null(TranslatorRegistry.Build("", new TranslatorOptions()));
    }
}
