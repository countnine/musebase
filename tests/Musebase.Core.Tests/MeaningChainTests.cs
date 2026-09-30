using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Musebase.Core.Meaning;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 의미 엔진 체인 — "무료인 동안 Gemini, 모자라면 OpenRouter". Gemini는 선불 잔액이 떨어지면 402,
/// 무료 티어 한도면 429를 준다(2026-09 실측: 이 서버의 키는 402). 그 곡부터 곧바로 다음 엔진이
/// 받아야 하고, 막힌 엔진을 곡마다 다시 두드리지 않아야 한다.
/// </summary>
public class MeaningChainTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"musebase-meaning-chain-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 정리 실패는 무시 */ }
    }

    private static readonly IReadOnlyList<MeaningSource> Sources = [new("wiki", null, "about love")];

    private static MeaningOptions Options(
        string engine = "gemini", string? fallback = "openrouter",
        string? geminiKey = "AIza" + "0123456789012345678901234567890abcd", long geminiCap = 0, bool auto = true) => new(
        Engine: engine, Lang: "ko",
        GeminiApiKey: geminiKey, GeminiModel: null,
        OpenRouterApiKey: "sk-or-v1-" + new string('0', 64), OpenRouterModel: "google/gemini-2.5-flash-lite",
        GeniusToken: null, LastFmKey: null, LastFmSecret: null, MusixmatchKey: null,
        SpotifyClientId: null, SpotifyClientSecret: null,
        Sources: ["wikipedia"], BackfillLimit: 50, BackfillDelayMs: 0,
        Fallback: fallback, GeminiMonthlyCap: geminiCap, AutoGenerate: auto);

    private sealed class FakeWriter(string id, MeaningWriteResult result) : IMeaningWriter
    {
        public int Calls;
        public string EngineId => id;
        public string Model => id + "-model";

        public Task<MeaningWriteResult> WriteAsync(
            string title, string artist, IReadOnlyList<MeaningSource> sources, string targetLang, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(result);
        }
    }

    private static MeaningWriteResult Status(int code) =>
        MeaningWriteResult.FromStatus((System.Net.HttpStatusCode)code, "{\"error\":{\"message\":\"credits depleted\"}}");

    // ---- 체인 ----

    [Fact]
    public async Task 잔액이_없으면_다음_엔진이_쓰고_누가_썼는지_남긴다()
    {
        var gemini = new FakeWriter("gemini", Status(402));
        var openrouter = new FakeWriter("openrouter", MeaningWriteResult.Written("사랑에 대한 노래"));
        var service = new SongMeaningService(
            [new StubSource()], new CompositeMeaningWriter([gemini, openrouter]));

        var meaning = await service.BuildAsync("Song", "Artist", "ko");

        Assert.Equal(SongMeaning.Ok, meaning.Status);
        Assert.Equal("openrouter", meaning.Engine);            // 대표 id(gemini)가 아니라 실제로 쓴 엔진
        Assert.Equal("openrouter-model", meaning.Model);
    }

    [Fact]
    public async Task 빈_응답도_다음_엔진에_넘긴다()
    {
        var composite = new CompositeMeaningWriter(
        [
            new FakeWriter("gemini", MeaningWriteResult.Failed with { Reason = "응답 본문이 비어 있음" }),
            new FakeWriter("openrouter", MeaningWriteResult.Written("본문")),
        ]);

        var result = await composite.WriteAsync("t", "a", Sources, "ko");

        Assert.Equal("본문", result.Text);
    }

    [Fact]
    public async Task 쉬는_엔진은_부르지_않는다()
    {
        var gemini = new FakeWriter("gemini", MeaningWriteResult.Written("x"));
        var openrouter = new FakeWriter("openrouter", MeaningWriteResult.Written("y"));
        var composite = new CompositeMeaningWriter([gemini, openrouter], new MeaningChainHooks(Skip: id => id == "gemini"));

        var result = await composite.WriteAsync("t", "a", Sources, "ko");

        Assert.Equal(0, gemini.Calls);
        Assert.Equal("openrouter", result.Engine);
    }

    [Fact]
    public async Task 전부_쉬면_저장하지_않는_잠시_후_다시다()
    {
        var composite = new CompositeMeaningWriter(
            [new FakeWriter("gemini", MeaningWriteResult.Written("x"))], new MeaningChainHooks(Skip: _ => true));

        var result = await composite.WriteAsync("t", "a", Sources, "ko");

        Assert.Null(result.Text);
        Assert.True(result.Retryable);   // 곡을 "실패"로 굳히지 않는다
    }

    // ---- 구성 ----

    [Fact]
    public void 체인은_주_엔진_먼저_모르는_것과_중복은_버린다()
    {
        Assert.Equal(["gemini", "openrouter"], Options(fallback: "openrouter, gemini, bogus").ChainIds);
        Assert.Equal(["gemini"], Options(fallback: "none").ChainIds);   // 화면에서 폴백을 전부 끈 상태
    }

    [Fact]
    public void 주_엔진을_끄면_폴백도_없다() =>
        Assert.Empty(Options(engine: "none").ChainIds);

    [Fact]
    public void 키가_없는_엔진은_체인에서_빠진다()
    {
        var writer = Options(geminiKey: null).BuildWriter();

        Assert.Equal("openrouter", Assert.IsAssignableFrom<IMeaningWriter>(writer).EngineId);
    }

    // ---- 쉬게 하기·월 상한 ----

    [Fact]
    public void 잔액_없음은_30분_쉬고_시간이_지나면_다시_부른다()
    {
        using var store = new LyricsStore(_dbPath);
        var now = DateTimeOffset.Parse("2026-09-30T05:00:00Z");
        var gate = new MeaningEngineGate(store, () => now);

        gate.Record("gemini", Status(402));
        Assert.Contains("402", gate.Closed("gemini", Options()));

        now = now.AddMinutes(31);
        Assert.Null(gate.Closed("gemini", Options()));
    }

    [Fact]
    public void 한도_429는_10분_일시_오류는_세_번_연속이면_쉰다()
    {
        using var store = new LyricsStore(_dbPath);
        var now = DateTimeOffset.Parse("2026-09-30T05:00:00Z");
        var gate = new MeaningEngineGate(store, () => now);

        gate.Record("gemini", Status(429));
        Assert.NotNull(gate.Closed("gemini", Options()));
        now = now.AddMinutes(11);
        Assert.Null(gate.Closed("gemini", Options()));

        gate.Record("openrouter", MeaningWriteResult.Transient);
        gate.Record("openrouter", MeaningWriteResult.Transient);
        Assert.Null(gate.Closed("openrouter", Options()));
        gate.Record("openrouter", MeaningWriteResult.Transient);
        Assert.NotNull(gate.Closed("openrouter", Options()));
    }

    [Fact]
    public void 월_상한에_닿으면_Gemini를_건너뛴다()
    {
        using var store = new LyricsStore(_dbPath);
        var gate = new MeaningEngineGate(store);

        gate.Record("gemini", MeaningWriteResult.Written("a"));
        gate.Record("gemini", MeaningWriteResult.Written("b"));

        Assert.Equal(2, gate.UsedThisMonth("gemini"));
        Assert.Null(gate.Closed("gemini", Options(geminiCap: 0)));      // 0 = 세기만 한다
        Assert.Contains("상한", gate.Closed("gemini", Options(geminiCap: 2)));
    }

    [Fact]
    public void 설정을_저장하면_쉬던_엔진을_깨운다()
    {
        using var store = new LyricsStore(_dbPath);
        var settings = new MeaningSettings(store, Options());
        settings.Gate.Record("gemini", Status(402));

        settings.Save(engine: "gemini", geminiKey: null, geminiModel: null, openRouterKey: null, openRouterModel: null);

        Assert.Null(settings.Gate.Closed("gemini", settings.Current));
    }

    [Fact]
    public void 폴백을_전부_끄면_환경변수의_폴백이_되살아나지_않는다()
    {
        using var store = new LyricsStore(_dbPath);
        var settings = new MeaningSettings(store, Options(fallback: "openrouter"));

        settings.Save(engine: null, geminiKey: null, geminiModel: null, openRouterKey: null, openRouterModel: null, fallback: "");

        Assert.Equal(["gemini"], settings.Current.ChainIds);
    }

    // ---- 폼·키 형식 ----

    [Theory]
    [InlineData("AIza0123456789012345678901234567890abcd")]
    [InlineData("AQ.Ab8RN6" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH")]
    public void Gemini_키는_두_형식을_받는다(string key) =>
        Assert.Null(MeaningCheckBook.KeyFormatProblem("gemini", key));

    [Fact]
    public void Gemini_칸에_OpenRouter_키를_넣으면_걸린다()
    {
        var why = MeaningCheckBook.KeyFormatProblem("gemini", "sk-or-v1-" + new string('0', 64));

        Assert.NotNull(why);
        Assert.DoesNotContain("0000", why);   // 비밀 본문은 말하지 않는다
    }

    private static IFormCollection Form(params (string Key, string[] Values)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new StringValues(f.Values)));

    [Fact]
    public void 폼은_폴백_자동생성_상한을_읽는다()
    {
        var form = MeaningForm.Read(Form(
            ("fallbackSubmitted", ["1"]), ("engine", ["gemini"]), ("fallback", ["openrouter"]),
            ("auto", ["1"]), ("geminiCap", ["900"]), ("autoCap", ["200"])));

        Assert.Equal("openrouter", form.Fallback);
        Assert.True(form.Auto);
        Assert.Equal(900, form.GeminiCap);
        Assert.Equal(200, form.AutoCap);
    }

    [Fact]
    public void 테스트용_구성은_빈_칸이면_지금_값을_쓰고_체인과_새_키를_확인한다()
    {
        var input = MeaningForm.Read(Form(("engine", ["openrouter"])));
        var trial = input.Overlay(Options());

        Assert.Equal("openrouter", trial.Engine);
        Assert.Equal(Options().GeminiApiKey, trial.GeminiApiKey);
        Assert.Equal(["openrouter"], input.EnginesToCheck(trial));   // 폴백 openrouter는 주 엔진과 같아 빠진다
    }

    // ---- 화면 ----

    private static string Card(IReadOnlyDictionary<string, EngineCheck>? checks = null, IReadOnlyDictionary<string, string>? paused = null) =>
        AdminPages.Dashboard(
            new DashboardModel(
                new ServerStats(0, 0, null), 0, new HitRate(0, 0, 0), new HitRate(0, 0, 0),
                [], [], [], [], [], [], [], [],
                new ServerHealth(TimeSpan.FromHours(1), 0, 0, 90), [],
                new MeaningSummary(0, 0, 0, 0, false), [], "csrf-token")
            {
                MeaningEngine = MeaningEngineCard.From(Options(), false, checks: checks) with { Paused = paused },
            },
            DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

    [Fact]
    public void 의미_카드도_쓰는_순서대로_폴백은_폼_안에()
    {
        var html = Card();
        var card = html[html.IndexOf("<h2>의미 생성 엔진</h2>", StringComparison.Ordinal)..];
        var order = new[] { "① 주 엔진 고르기", "② 키 넣기", "③ 폴백", "④ 그 밖의 설정" }
            .Select(s => card.IndexOf(s, StringComparison.Ordinal)).ToList();
        var form = Regex.Match(card, "<form method=\"post\" action=\"/musebase/meanings/engine\">(.*?)</form>",
            RegexOptions.Singleline).Groups[1].Value;

        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.Contains("name=\"fallback\" value=\"openrouter\" checked", form);
        Assert.Contains("name=\"geminiCap\"", form);
        Assert.Contains("formaction=\"/musebase/meanings/engine/test\"", form);
        Assert.Contains(".mcard:has(input[name=engine][value=gemini]:checked) .k-gemini", html);
    }

    [Fact]
    public void 확인_결과와_쉬는_이유가_엔진_옆에_보인다()
    {
        var html = Card(
            new Dictionary<string, EngineCheck>
            {
                ["gemini"] = new("gemini", false, "402 — 결제 잔액이 없습니다", 120, DateTimeOffset.UtcNow, true),
                ["openrouter"] = new("openrouter", true, "ok", 900, DateTimeOffset.UtcNow, true),
            },
            new Dictionary<string, string> { ["gemini"] = "HTTP 402 — 05:30 UTC까지 쉼" });

        Assert.Contains("✗ 실패", html);
        Assert.Contains("402 — 결제 잔액이 없습니다", html);
        Assert.Contains("✓ 정상 · 900ms", html);
        Assert.Contains("쉬는 중 — HTTP 402", html);
    }

    // ---- 자동 생성 ----

    [Fact]
    public void 새_곡은_큐에_넣고_의미가_있거나_꺼져_있으면_넣지_않는다()
    {
        using var store = new LyricsStore(_dbPath);
        var settings = new MeaningSettings(store, Options());
        var generator = new MeaningGenerator(store, settings, NullLogger<MeaningGenerator>.Instance);
        var auto = new AutoMeaning(store, settings, generator, NullLogger<AutoMeaning>.Instance);
        store.UpsertMeaning(new MeaningEntry
        {
            Key = "done|artist", Title = "Done", Artist = "Artist", Lang = "ko", Sources = "[]",
            Status = MeaningEntry.StatusOk, Summary = "s", UpdatedAt = "2026-09-30T00:00:00Z",
        });

        Assert.True(auto.Offer("new|artist"));
        Assert.True(auto.IsPending("new|artist"));        // 앱 조회에 "만드는 중"이 간다
        Assert.False(auto.Offer("new|artist"));           // 같은 곡은 한 번만
        Assert.False(auto.Offer("done|artist"));          // 이미 행이 있다

        settings.Save(engine: null, geminiKey: null, geminiModel: null, openRouterKey: null, openRouterModel: null, auto: false);
        Assert.False(auto.Offer("other|artist"));
    }

    private sealed class StubSource : ISongMeaningSource
    {
        public string Name => "Stub";
        public Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default) =>
            Task.FromResult<MeaningSource?>(new MeaningSource("Stub", null, "about love"));
    }
}
