using Microsoft.Extensions.Logging.Abstractions;
using Musebase.Core.Search;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 기기가 올린 새 가사를 서버가 곧바로 번역하는 워커. 사람이 누르지 않는 경로라
/// <b>가드가 전부다</b> — 여기가 새면 매 업로드마다 돈이 나간다.
/// 실제 엔진은 부르지 않는다(키 없이 판정 규칙만 본다).
/// </summary>
public class AutoTranslatorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"musebase-auto-test-{Guid.NewGuid():N}.db");

    private static TranslationOptions Options(bool auto = true, string engine = "deepl", string? key = "k:fx") => new(
        Engine: engine, Lang: "KO", DeeplApiKey: key, GoogleApiKey: null, MyMemoryEmail: null,
        LibreEndpoint: null, LibreApiKey: null, OpenRouterApiKey: null, OpenRouterModel: null,
        BatchLimit: 30, DelayMs: 0, CharBudget: 50_000, AutoTranslate: auto);

    private (LyricsStore Store, AutoTranslator Auto) New(TranslationOptions options)
    {
        var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var settings = new TranslationSettings(store, options, cache);
        var generator = new TranslationGenerator(store, settings, cache, NullLogger<TranslationGenerator>.Instance);
        return (store, new AutoTranslator(store, settings, generator, NullLogger<AutoTranslator>.Instance));
    }

    [Fact]
    public void 한국어가_없는_곡은_큐에_넣는다()
    {
        var (store, auto) = New(Options());
        using var _ = store;

        Assert.True(auto.Offer("kids|mgmt", []));
        Assert.True(auto.Offer("time|pink floyd", ["*"]));   // 언어 미상 제공자 번역만 있다
        Assert.Equal(2, auto.Pending);
    }

    [Fact]
    public void 기기가_이미_번역해_올렸으면_넣지_않는다()
    {
        // PC가 자기 DeepL 키로 번역해 올리는 경우 — 서버가 또 번역하면 돈만 두 번 든다.
        var (store, auto) = New(Options());
        using var _ = store;

        Assert.False(auto.Offer("kids|mgmt", ["ko"]));
        Assert.False(auto.Offer("time|pink floyd", ["*", "KO"]));
        Assert.Equal(0, auto.Pending);
    }

    [Fact]
    public void 같은_곡은_한_번만_넣는다()
    {
        // 기기 둘이 같은 곡을 연달아 올리는 일이 흔하다.
        var (store, auto) = New(Options());
        using var _ = store;

        Assert.True(auto.Offer("kids|mgmt", []));
        Assert.False(auto.Offer("kids|mgmt", []));
        Assert.Equal(1, auto.Pending);
    }

    [Fact]
    public void 꺼져_있으면_아무것도_넣지_않는다()
    {
        var (store, auto) = New(Options(auto: false));
        using var _ = store;

        Assert.False(auto.Enabled);
        Assert.False(auto.Offer("kids|mgmt", []));
    }

    [Fact]
    public void 엔진이_없으면_켜_둬도_돌지_않는다()
    {
        // 기본 엔진은 none이다 — 켜 둔 것만으로 외부 API를 두드리기 시작하면 안 된다.
        var (store, auto) = New(Options(engine: "none", key: null));
        using var _ = store;

        Assert.False(auto.Enabled);
        Assert.False(auto.Offer("kids|mgmt", []));
    }

    [Fact]
    public void 월_상한에_닿으면_큐에서_꺼내도_번역하지_않는다()
    {
        var (store, auto) = New(Options() with { AutoMonthlyCap = 100 });
        using var db = store;
        store.Upsert(new LyricsEntry
        {
            Title = "Kids", Artist = "MGMT", Lrc = "[00:01.00]hello\n", Service = "LRCLIB",
            Origin = LyricsEntry.OriginProvider,
        }, "pc", out _);
        store.AddUsage(AutoTranslator.MeterName, 100, TranslationQuota.Month());

        auto.Offer(LyricsCacheStore.MakeKey("Kids", "MGMT"), []);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var loop = auto.RunAsync(cts.Token);
        SpinWait.SpinUntil(() => auto.Pending == 0, TimeSpan.FromSeconds(2));
        Thread.Sleep(300);   // 큐가 비면 워커가 무더기 요약을 남긴다 — 시도했다면 여기서 생긴다
        cts.Cancel();

        // 상한이라 **엔진을 부르지 않았다.** 시도했다면(실패했더라도) 번역 작업이 만들어져
        // 요약이 남는다 — 요약이 없다는 것이 "부르지 않았다"의 증거다.
        Assert.Null(auto.LastReport);
        Assert.Equal(1, store.GetByKey(LyricsCacheStore.MakeKey("Kids", "MGMT"))!.Revision);
        Assert.Equal(100, store.UsageThisMonth(AutoTranslator.MeterName, TranslationQuota.Month()));
    }

    [Fact]
    public void 상한_아래면_큐에서_꺼내_시도한다()
    {
        // 위 테스트의 대조군 — 상한만 풀면 시도가 일어나 요약이 남아야 한다.
        // 외부로 나가지 않도록 닫힌 로컬 포트를 엔진 주소로 준다(즉시 연결 거부 → 실패로 센다).
        var (store, auto) = New(
            Options(engine: "libretranslate", key: null) with { LibreEndpoint = "http://127.0.0.1:1" });
        using var db = store;
        store.Upsert(new LyricsEntry
        {
            Title = "Kids", Artist = "MGMT", Lrc = "[00:01.00]hello\n", Service = "LRCLIB",
            Origin = LyricsEntry.OriginProvider,
        }, "pc", out _);

        auto.Offer(LyricsCacheStore.MakeKey("Kids", "MGMT"), []);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = auto.RunAsync(cts.Token);
        SpinWait.SpinUntil(() => auto.LastReport is not null, TimeSpan.FromSeconds(15));
        cts.Cancel();

        Assert.NotNull(auto.LastReport);
        Assert.Contains("0/1곡", auto.LastReport);
    }

    [Fact]
    public void 무료_한도를_이미_채운_엔진은_시작_전에_닫힌다()
    {
        using var store = new LyricsStore(_dbPath);
        store.AddUsage("deepl", 500_000, TranslationQuota.Month());

        var run = TranslationRun.Begin(Options(), store, NullLogger.Instance);

        Assert.True(run.Breaker.IsOpen("deepl"));
    }

    [Fact]
    public void 무료_한도에_닿은_엔진은_작업_도중에도_닫힌다()
    {
        // 시작할 때만 보면 한 작업 안에서 50만 자를 넘겨도 계속 Google로 간다(조용히 과금된다).
        using var store = new LyricsStore(_dbPath);
        store.AddUsage("deepl", 499_990, TranslationQuota.Month());

        var run = TranslationRun.Begin(Options(), store, NullLogger.Instance);
        Assert.False(run.Breaker.IsOpen("deepl"));   // 시작할 땐 아직 한도 전

        run.RecordFilled("deepl", lines: 3, chars: 20);   // 체인이 채움을 보고한다

        Assert.True(run.Breaker.IsOpen("deepl"));
        Assert.Equal(500_010, store.UsageThisMonth("deepl", TranslationQuota.Month()));
    }

    [Fact]
    public void 한도를_모르는_엔진은_미터가_닫지_않는다()
    {
        // OpenRouter는 무료 한도가 없다 — 한도를 지어내 멈추면 폴백 꼬리가 사라진다.
        using var store = new LyricsStore(_dbPath);
        var run = TranslationRun.Begin(Options(), store, NullLogger.Instance);

        run.RecordFilled("openrouter", lines: 1000, chars: 900_000);

        Assert.False(run.Breaker.IsOpen("openrouter"));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* 임시 파일 */ }
    }
}
