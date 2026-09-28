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

    /// <summary>
    /// 레지스트리로 만든 번역기는 <b>넘겨준 HttpClient를 쓴다</b>. 서버의 일괄 번역은 오래 걸려도
    /// 되는 호출이라 여유 있는 클라이언트를 넣는데, 이 통로가 막히면 기본값(15초)이 LLM 번역의
    /// 예산을 몰래 자른다(의미 생성 쪽에서 이미 겪은 함정이다).
    /// </summary>
    [Fact]
    public async Task TranslatorBuild_UsesInjectedHttpClient()
    {
        var calls = 0;
        var http = new HttpClient(new CountingHandler(() => calls++));

        var google = TranslatorRegistry.Build("google", new TranslatorOptions(GoogleApiKey: "AIza-test", Http: http));
        await google!.TranslateAsync(["hello"], "KO");

        Assert.Equal(1, calls);
    }

    /// <summary>넘기지 않으면 기본 클라이언트를 쓴다 — 앱 동작이 그대로여야 한다.</summary>
    [Fact]
    public void TranslatorOptions_HttpIsOptional() =>
        Assert.Null(new TranslatorOptions(GoogleApiKey: "k").Http);

    private sealed class CountingHandler(Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onCall();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"translations":[{"translatedText":"안녕"}]}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
