using Musebase.Core.Translation;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// LLM 번역기는 기계 번역기와 실패 방식이 다르다 — 줄을 합치거나 설명을 덧붙인다.
/// 가사는 줄이 곧 타이밍이라 하나만 밀려도 화면 전체가 어긋나므로, 여기 검사가 마지막 방어선이다.
/// </summary>
public class OpenRouterTranslatorTests
{
    [Fact]
    public void 줄_수가_맞으면_그대로_받는다()
    {
        var parsed = OpenRouterTranslator.ParseLines("""["안녕","잘 가"]""", expected: 2);

        Assert.NotNull(parsed);
        Assert.Equal(["안녕", "잘 가"], parsed!);
    }

    [Theory]
    [InlineData("""["하나"]""")]                    // 줄을 합쳤다
    [InlineData("""["하나","둘","셋"]""")]          // 없던 줄을 만들었다
    public void 줄_수가_다르면_통째로_버린다(string content)
    {
        // 반쯤 맞은 번역은 없는 것보다 나쁘다 — 원문과 어긋난 자막이 계속 따라다닌다.
        Assert.Null(OpenRouterTranslator.ParseLines(content, expected: 2));
    }

    [Fact]
    public void 코드펜스를_둘러도_읽는다()
    {
        // ```json 으로 감싸는 모델이 흔하다.
        var content = "```json\n[\"안녕\",\"잘 가\"]\n```";

        var parsed = OpenRouterTranslator.ParseLines(content, expected: 2);

        Assert.NotNull(parsed);
        Assert.Equal("안녕", parsed![0]);
    }

    [Theory]
    [InlineData("여기 번역입니다: [\"안녕\"]")]        // 설명을 덧붙였다
    [InlineData("""{"lines":["안녕"]}""")]            // 배열이 아니라 객체로 줬다
    [InlineData("안녕")]                              // JSON이 아니다
    [InlineData("")]
    public void JSON_배열이_아니면_버린다(string content) =>
        Assert.Null(OpenRouterTranslator.ParseLines(content, expected: 1));

    [Fact]
    public void 빈_줄은_null로_둔다()
    {
        // 간주처럼 옮길 것이 없는 줄. 빈 문자열을 그대로 쓰면 번역이 있는 줄로 세어진다.
        var parsed = OpenRouterTranslator.ParseLines("""["","안녕"]""", expected: 2);

        Assert.NotNull(parsed);
        Assert.Null(parsed![0]);
        Assert.Equal("안녕", parsed[1]);
    }

    [Theory]
    [InlineData("KO", "Korean")]
    [InlineData("ZH-HANT", "Traditional Chinese")]
    [InlineData("zh-hans", "Simplified Chinese")]
    [InlineData("EN-US", "English")]
    [InlineData("PT-BR", "Portuguese")]
    public void 언어_코드_대신_이름을_넘긴다(string code, string expected) =>
        Assert.Equal(expected, OpenRouterTranslator.LanguageName(code));

    [Fact]
    public void 모르는_코드는_그대로_넘긴다()
    {
        // 모델이 알아보는 경우가 많고, 틀려도 줄 수 검사가 막아 준다.
        Assert.Equal("XX", OpenRouterTranslator.LanguageName("xx"));
        Assert.Equal("English", OpenRouterTranslator.LanguageName(""));
        Assert.Equal("English", OpenRouterTranslator.LanguageName(null));
    }

    [Fact]
    public async Task 키가_없으면_조용히_번역_없음이다()
    {
        // 키를 안 넣었다고 예외가 나면 가사 표시까지 막힌다.
        var translator = new OpenRouterTranslator("");

        var result = await translator.TranslateAsync(["hello", "world"], "KO");

        Assert.Equal(2, result.Count);
        Assert.All(result, Assert.Null);
    }

    [Fact]
    public void 레지스트리에_등록돼_있고_키가_없으면_만들어지지_않는다()
    {
        var descriptor = TranslatorRegistry.Find("openrouter");
        Assert.NotNull(descriptor);
        Assert.True(descriptor!.RequiresApiKey);

        Assert.Null(TranslatorRegistry.Build("openrouter", new TranslatorOptions()));
        Assert.NotNull(TranslatorRegistry.Build("openrouter", new TranslatorOptions(OpenRouterApiKey: "sk-or-x")));
    }

    [Fact]
    public void 모델을_비우면_기본_모델을_쓴다()
    {
        Assert.Equal(OpenRouterTranslator.DefaultModel, new OpenRouterTranslator("k").Model);
        Assert.Equal(OpenRouterTranslator.DefaultModel, new OpenRouterTranslator("k", "  ").Model);
        Assert.Equal("anthropic/claude-opus-5", new OpenRouterTranslator("k", " anthropic/claude-opus-5 ").Model);
    }
}
