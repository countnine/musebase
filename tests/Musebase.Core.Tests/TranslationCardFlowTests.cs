using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 번역 엔진 설정 화면 — 쓰는 순서대로 보이는가, 고른 엔진의 키만 받는가, 키가 맞는지 가려내는가.
/// 실제로 겪은 사고(Google 칸에 다른 자격증명 → 6곡 조용히 건너뜀, 저장할 때마다 폴백이 지워짐)를
/// 다시 겪지 않는 것이 이 테스트들의 목적이다.
/// </summary>
public class TranslationCardFlowTests
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");

    private static TranslationOptions Options() => new(
        Engine: "google", Lang: "KO", DeeplApiKey: null, GoogleApiKey: "AIza" + new string('x', 35),
        MyMemoryEmail: null, LibreEndpoint: null, LibreApiKey: null,
        OpenRouterApiKey: "sk-or-v1-" + new string('a', 64), OpenRouterModel: "google/gemini-2.5-flash-lite",
        BatchLimit: 30, DelayMs: 0, CharBudget: 50_000, Fallback: "openrouter");

    // ---- 키 형식 ----

    [Fact]
    public void 구글_칸에_API_키가_아닌_값을_넣으면_형식에서_걸린다()
    {
        // 실제로 들어갔던 값의 모양 — AQ.A로 시작하는 53자.
        var why = TranslationOptions.KeyFormatProblem("google", "AQ.Ab8RN" + new string('z', 45));

        Assert.NotNull(why);
        Assert.Contains("AIza", why);
        Assert.Contains("53자", why);
        Assert.DoesNotContain("zzzz", why);   // 비밀 본문은 알림·로그에 새지 않는다
    }

    [Theory]
    [InlineData("google", "AIzaSyA1234567890abcdefghijklmnopqrstuv")]
    [InlineData("deepl", "12345678-90ab-cdef-1234-567890abcdef:fx")]
    [InlineData("deepl", "12345678-90ab-cdef-1234-567890abcdef")]
    public void 맞는_형식은_통과한다(string engine, string key) =>
        Assert.Null(TranslationOptions.KeyFormatProblem(engine, key));

    [Fact]
    public void 오픈라우터_형식도_통과한다() =>
        // 진짜처럼 생긴 문자열을 소스에 두면 비밀 스캐너가 막는다 — 실행 중에 만든다.
        Assert.Null(TranslationOptions.KeyFormatProblem("openrouter", "sk-or-v1-" + new string('0', 64)));

    [Theory]
    [InlineData("deepl", "not-a-key")]
    [InlineData("openrouter", "sk-proj-abc")]
    [InlineData("google", "AIza-too-short")]
    public void 틀린_형식은_이유와_함께_걸린다(string engine, string key) =>
        Assert.NotNull(TranslationOptions.KeyFormatProblem(engine, key));

    [Fact]
    public void 형식이_자유로운_엔진은_판정하지_않는다() =>
        Assert.Null(TranslationOptions.KeyFormatProblem("libretranslate", "아무거나"));

    // ---- 폼 해석 ----

    private static IFormCollection Form(params (string Key, string[] Values)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new StringValues(f.Values)));

    [Fact]
    public void 형식이_틀린_키는_비워서_저장하지_않는다()
    {
        var (clean, rejected) = TranslationForm.Read(Form(
            ("engine", ["google"]),
            ("googleKey", ["AQ.Ab8RN" + new string('z', 45)]),
            ("deeplKey", ["12345678-90ab-cdef-1234-567890abcdef:fx"]))).WithoutMalformedKeys();

        Assert.Equal("", clean.GoogleKey);    // 빈 칸 = "그대로 두기" → 틀린 값으로 덮지 않는다
        Assert.Equal("12345678-90ab-cdef-1234-567890abcdef:fx", clean.DeeplKey);
        Assert.Equal("google", Assert.Single(rejected).Engine);
    }

    [Fact]
    public void 폴백_체크를_전부_끄면_폴백이_지워진다()
    {
        // 체크박스는 꺼져 있으면 전송되지 않는다 — 숨은 마커만 오면 "폴백 없음"이다.
        var form = TranslationForm.Read(Form(("fallbackSubmitted", ["1"]), ("engine", ["google"])));

        Assert.Equal("", form.Fallback);
        Assert.False(form.Auto);
    }

    [Fact]
    public void 마커가_없으면_폴백과_자동_번역을_건드리지_않는다()
    {
        var form = TranslationForm.Read(Form(("engine", ["google"])));

        Assert.Null(form.Fallback);
        Assert.Null(form.Auto);
    }

    [Fact]
    public void 테스트용_구성은_빈_칸이면_지금_값을_쓴다()
    {
        var now = Options();
        var trial = TranslationForm.Read(Form(
            ("engine", ["deepl"]),
            ("deeplKey", ["12345678-90ab-cdef-1234-567890abcdef:fx"]))).Overlay(now);

        Assert.Equal("deepl", trial.Engine);
        Assert.Equal("12345678-90ab-cdef-1234-567890abcdef:fx", trial.DeeplApiKey);
        Assert.Equal(now.GoogleApiKey, trial.GoogleApiKey);   // 안 건드린 칸은 그대로
        Assert.Equal("openrouter", trial.Fallback);           // 마커가 없으면 폴백도 그대로
    }

    [Fact]
    public void 확인할_엔진은_체인과_새로_넣은_키다()
    {
        var form = TranslationForm.Read(Form(("deeplKey", ["12345678-90ab-cdef-1234-567890abcdef:fx"])));

        var engines = form.EnginesToCheck(Options());

        Assert.Equal(["google", "openrouter", "deepl"], engines);
    }

    // ---- 화면 ----

    private static string Card(IReadOnlyDictionary<string, EngineCheck>? checks = null) =>
        AdminPages.Dashboard(
            EmptyDashboard() with
            {
                TranslationEngine = TranslationEngineCard.From(Options(), false, 0, checks: checks),
            },
            DateTimeOffset.UtcNow, Kst);

    private static DashboardModel EmptyDashboard() => new(
        new ServerStats(0, 0, null), 0, new HitRate(0, 0, 0), new HitRate(0, 0, 0),
        [], [], [], [], [], [], [], [],
        new ServerHealth(TimeSpan.FromHours(1), 0, 0, 90), [],
        new MeaningSummary(0, 0, 0, 0, false), [], "csrf-token");

    [Fact]
    public void 쓰는_순서대로_단계가_보인다()
    {
        var html = Card();
        var order = new[] { "① 주 엔진 고르기", "② 키 넣기", "③ 폴백", "④ 그 밖의 설정" }
            .Select(s => html.IndexOf(s, StringComparison.Ordinal)).ToList();

        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void 폴백_체크박스는_폼_안에_있다()
    {
        // 예전에는 폼 바깥에 그려져 저장할 때마다 폴백이 지워졌다.
        var html = Card();
        var form = Regex.Match(html, "<form method=\"post\" action=\"/musebase/translate/engine\">(.*?)</form>",
            RegexOptions.Singleline).Groups[1].Value;

        Assert.Contains("name=\"fallback\"", form);
        Assert.Contains("name=\"auto\"", form);
        Assert.Contains("name=\"googleKey\"", form);
    }

    [Fact]
    public void 고른_엔진의_키_칸만_보이게_한다()
    {
        var html = Card();

        // 기본은 숨기고, 고른 엔진·체크한 폴백의 칸만 연다(:has를 모르면 전부 보인다).
        Assert.Contains("@supports selector(:has(a))", html);
        Assert.Contains(".tcard:has(input[name=engine][value=google]:checked) .k-google", html);
        Assert.Contains(".tcard:has(input[name=fallback][value=openrouter]:checked) .k-openrouter", html);
        Assert.Contains("class=\"keyrow k-deepl\"", html);
    }

    [Fact]
    public void 공개_무키_엔진은_폴백으로_고를_수_없다()
    {
        var html = Card();

        Assert.DoesNotContain("name=\"fallback\" value=\"mymemory\"", html);
        Assert.DoesNotContain("name=\"fallback\" value=\"libretranslate\"", html);
    }

    [Fact]
    public void 키_확인_결과가_엔진_옆에_보인다()
    {
        var at = DateTimeOffset.Parse("2026-09-30T01:02:00Z");
        var html = Card(new Dictionary<string, EngineCheck>
        {
            ["google"] = new("google", true, "\"hello\" → \"안녕하세요\"", 230, at, Saved: true),
            ["deepl"] = new("deepl", false, "403 — 권한이 없습니다", 90, at, Saved: false),
        });

        Assert.Contains("✓ 정상 · 230ms", html);
        Assert.Contains("✗ 실패", html);
        Assert.Contains("403 — 권한이 없습니다", html);
        Assert.Contains("저장 전 테스트", html);
        Assert.Contains("확인 안 함", html);   // 확인한 적 없는 엔진
    }

    [Fact]
    public void 저장하지_않고_테스트하는_버튼이_있다() =>
        Assert.Contains("formaction=\"/musebase/translate/engine/test\"", Card());

    // ---- 확인 ----

    [Fact]
    public async Task 형식이_틀린_키는_부르지_않고_형식으로_실패한다()
    {
        var book = new EngineCheckBook();
        var bad = Options() with { GoogleApiKey = "AQ.Ab8RN" + new string('z', 45) };

        var result = Assert.Single(await book.CheckAsync(bad, ["google"], saved: true));

        Assert.False(result.Ok);
        Assert.Equal(0, result.Millis);      // 호출하지 않았다
        Assert.Contains("AIza", result.Detail);
        Assert.Same(result, book.Last["google"]);
    }

    [Fact]
    public async Task 키가_없으면_키가_없다고_말한다()
    {
        var book = new EngineCheckBook();

        var result = Assert.Single(await book.CheckAsync(Options(), ["deepl"], saved: false));

        Assert.False(result.Ok);
        Assert.Contains("키가 없습니다", result.Detail);
    }
}
