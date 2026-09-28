using Microsoft.Extensions.Logging.Abstractions;
using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 서버가 직접 가사를 번역해 되쓰는 경로. 여기는 <b>돈과 원본</b>이 걸린 자리다 —
/// 추정이 실제와 갈리면 비용이 새고, 재직렬화가 원본을 깎으면 가사가 망가진다.
/// </summary>
public class ServerTranslationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"musebase-srvtr-test-{Guid.NewGuid():N}.db");

    private const string Plain = "[00:01.00]hello\n[00:05.00]world\n";

    private sealed class FakeTranslator(string prefix = "번역:") : ITranslator
    {
        public List<string> Requested { get; } = [];

        public Task<IReadOnlyList<string?>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
        {
            Requested.AddRange(texts);
            return Task.FromResult<IReadOnlyList<string?>>(texts.Select(t => (string?)(prefix + t)).ToList());
        }
    }

    /// <summary>실제 엔진 대신 스텁을 물린 서비스 공급자 — 네트워크 없이 경로 전체를 본다.</summary>
    private sealed class StubSource(ITranslator translator, ITranslationCache cache) : ITranslationServiceSource
    {
        public LyricsTranslationService Service { get; } = new(translator, cache);
        public bool IsEnabled => true;
    }

    private static LyricsEntry Entry(string title, string artist, string lrc) =>
        new() { Title = title, Artist = artist, Lrc = lrc, Service = "LRCLIB", Origin = LyricsEntry.OriginProvider };

    // ---- 재직렬화 왕복 ----

    [Theory]
    // 다중 타임태그 — 파싱하면 줄로 펼쳐진다(줄 수가 늘어도 깎이는 것은 아니다).
    [InlineData("[00:10.00][01:20.00]같은 줄\n")]
    // 글자단위 카라오케(tt) — 품질 랭킹에서 가장 무거운 자산이라 절대 잃으면 안 된다.
    [InlineData("[00:01.00]hello\n[00:01.00][tt]<0,500>hel<500,500>lo\n")]
    // 언어 미상 제공자 번역
    [InlineData("[00:01.00]hello\n[00:01.00][tr]你好\n")]
    // id 태그
    [InlineData("[ti:Kids]\n[ar:MGMT]\n[00:01.00]hello\n")]
    public void 파싱_재직렬화_왕복에서_사실이_깎이지_않는다(string lrc)
    {
        var before = LyricsFacts.From(lrc);
        var after = LyricsFacts.From(Lyrics.Parse(lrc)!.ToString());

        // 줄 수가 줄거나 글자단위 타임태그가 사라지면 번역 잡이 저장을 거부한다(= 그 형태는 못 돌린다).
        Assert.True(after.LineCount >= before.LineCount, $"줄 수 {before.LineCount} → {after.LineCount}");
        Assert.Equal(before.HasInlineTimeTags, after.HasInlineTimeTags);
        Assert.Equal(before.Langs, after.Langs);
    }

    // ---- 추정 ----

    [Fact]
    public void 추정은_캐시에_있는_줄과_이미_번역된_줄을_뺀다()
    {
        using var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var generator = Generator(store, cache, new FakeTranslator());

        store.Upsert(Entry("Kids", "MGMT", "[00:01.00]hello\n[00:05.00]world\n[00:09.00]hello\n"), "pc", out _);
        var key = LyricsCacheStore.MakeKey("Kids", "MGMT");

        // hello(5) + world(5) — 같은 원문이 두 번 나와도 요청은 한 번이다.
        Assert.Equal(10, generator.Estimate(key, "KO"));

        cache.Set("hello", "KO", "안녕");
        Assert.Equal(5, generator.Estimate(key, "KO"));   // 캐시 적중분은 돈이 들지 않는다
    }

    [Fact]
    public void 이미_대상_언어_번역이_있으면_추정이_0이다()
    {
        using var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var generator = Generator(store, cache, new FakeTranslator());

        store.Upsert(Entry("Kids", "MGMT",
            "[00:01.00]hello\n[00:01.00][tr:ko]안녕\n"), "pc", out _);

        Assert.Equal(0, generator.Estimate(LyricsCacheStore.MakeKey("Kids", "MGMT"), "KO"));
    }

    // ---- 번역·저장 ----

    [Fact]
    public async Task 번역하면_대상_언어_태그가_붙고_provider로_저장된다()
    {
        using var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var generator = Generator(store, cache, new FakeTranslator());

        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);
        var key = LyricsCacheStore.MakeKey("Kids", "MGMT");

        var outcome = await generator.TranslateAsync(key, "KO");

        Assert.Equal("ok", outcome.Status);
        Assert.Equal(2, outcome.ChangedLines);

        var saved = store.GetByKey(key)!;
        Assert.Contains("[tr:ko]번역:hello", saved.Lrc);
        Assert.Equal(["ko"], saved.Langs!);
        // user로 저장하면 이후 기기 업로드가 전부 user-edit-protected로 막힌다.
        Assert.Equal(LyricsEntry.OriginProvider, saved.Origin);
        Assert.Equal(2, saved.Revision);

        // 기기가 올린 더 좋은 가사가 여전히 들어올 수 있어야 한다 — 서버 번역본이
        // "사용자 편집본"으로 굳으면 이후 업로드가 전부 막힌다(그것이 OriginUser의 뜻이다).
        var better = Plain
            .Replace("hello\n", "hello\n[00:01.00][tr:ko]안녕\n")
            .Replace("world\n", "world\n[00:05.00][tr:ko]세상\n")
            + "[00:09.00]more\n";
        var newer = store.Upsert(Entry("Kids", "MGMT", better), "안드로이드", out var rejection);
        Assert.Null(rejection);
        Assert.NotNull(newer);

        // 더 빈약한 업로드는 병합 정책이 막는다 — 그 사유가 user-edit-protected면 안 된다.
        store.Upsert(Entry("Kids", "MGMT", Plain), "안드로이드", out var poorer);
        Assert.Equal("poorer-content", poorer?.Reason);
    }

    [Fact]
    public async Task 이미_번역된_곡은_저장하지_않는다()
    {
        using var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var generator = Generator(store, cache, new FakeTranslator());

        store.Upsert(Entry("Kids", "MGMT",
            "[00:01.00]hello\n[00:01.00][tr:ko]안녕\n"), "pc", out _);
        var key = LyricsCacheStore.MakeKey("Kids", "MGMT");
        var before = store.GetByKey(key)!.Revision;

        var outcome = await generator.TranslateAsync(key, "KO");

        // 재직렬화만 하고 revision을 올리는 최악을 원천 차단한다.
        Assert.Equal("skipped", outcome.Status);
        Assert.Equal(before, store.GetByKey(key)!.Revision);
    }

    [Fact]
    public async Task 번역한_줄은_캐시에_남아_다음_곡에서_공짜다()
    {
        using var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var translator = new FakeTranslator();
        var generator = Generator(store, cache, translator);

        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);
        store.Upsert(Entry("Time", "Pink Floyd", Plain), "pc", out _);

        await generator.TranslateAsync(LyricsCacheStore.MakeKey("Kids", "MGMT"), "KO");
        var afterFirst = translator.Requested.Count;

        await generator.TranslateAsync(LyricsCacheStore.MakeKey("Time", "Pink Floyd"), "KO");

        Assert.Equal(2, afterFirst);
        Assert.Equal(2, translator.Requested.Count);   // 같은 원문이라 한 번도 더 부르지 않는다
        Assert.Equal(0, generator.Estimate(LyricsCacheStore.MakeKey("Time", "Pink Floyd"), "KO"));
    }

    [Fact]
    public async Task 읽지_못하는_LRC는_건너뛴다()
    {
        using var store = new LyricsStore(_dbPath);
        var cache = new StoreTranslationCache(store);
        var generator = Generator(store, cache, new FakeTranslator());

        store.Upsert(Entry("Kids", "MGMT", "타임태그가 없는 그냥 글"), "pc", out _);

        var outcome = await generator.TranslateAsync(LyricsCacheStore.MakeKey("Kids", "MGMT"), "KO");

        Assert.Equal("unparsable", outcome.Status);
    }

    // ---- 단가 ----

    [Fact]
    public void 문자로_환산할_수_없는_엔진은_비용을_지어내지_않는다()
    {
        // OpenRouter는 토큰 과금이라 문자 수로 환산하면 거짓말이 된다.
        Assert.Null(TranslationPricing.Estimate("openrouter", 1_000_000));
        Assert.Equal("무료 (한도 소진)", TranslationPricing.Estimate("mymemory", 1_000_000));
        Assert.Equal("약 $25.00", TranslationPricing.Estimate("deepl", 1_000_000));
        Assert.Equal("약 $0.01 미만", TranslationPricing.Estimate("deepl", 1));
    }

    [Fact]
    public void 단가는_환경변수로_덮을_수_있고_오타는_무시한다()
    {
        var prices = TranslationPricing.FromEnvironment("deepl=10, 이상한값, google=abc");

        Assert.Equal("약 $10.00", TranslationPricing.Estimate("deepl", 1_000_000, prices));
        Assert.Equal("약 $20.00", TranslationPricing.Estimate("google", 1_000_000, prices));  // 기본값 유지
    }

    private static TranslationGenerator Generator(
        LyricsStore store, ITranslationCache cache, ITranslator translator) =>
        new TranslationGenerator(
            store, new StubSource(translator, cache), cache, NullLogger<TranslationGenerator>.Instance);

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* 임시 파일 */ }
    }
}
