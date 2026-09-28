using Musebase.Core.Translation;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 서버의 번역 구성 — <c>server.env</c> 위에 관리 화면(DB) 값을 덮는 규칙.
/// 의미 생성 쪽과 같은 규칙이라야 사람이 두 카드를 다르게 기억하지 않는다.
/// </summary>
public class TranslationSettingsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"musebase-translate-test-{Guid.NewGuid():N}.db");

    private static readonly TranslationOptions Env = new(
        Engine: "deepl", Lang: "KO",
        DeeplApiKey: "env-deepl-key", GoogleApiKey: null, MyMemoryEmail: null,
        LibreEndpoint: null, LibreApiKey: null, OpenRouterApiKey: null, OpenRouterModel: null,
        BatchLimit: 30, DelayMs: 0, CharBudget: 50_000);

    private (LyricsStore Store, TranslationSettings Settings) New()
    {
        var store = new LyricsStore(_dbPath);
        return (store, new TranslationSettings(store, Env, new StoreTranslationCache(store)));
    }

    [Fact]
    public void 아무것도_저장하지_않으면_환경변수_그대로다()
    {
        var (store, settings) = New();
        using var _ = store;

        Assert.Equal("deepl", settings.Current.Engine);
        Assert.Equal("env-deepl-key", settings.Current.DeeplApiKey);
        Assert.False(settings.Overridden);
        Assert.True(settings.Current.IsEnabled);
    }

    [Fact]
    public void 화면에서_저장한_값이_환경변수를_덮고_되돌리면_원래로_간다()
    {
        var (store, settings) = New();
        using var _ = store;

        settings.Save("google", "EN-US", null, "db-google-key", null, null, null, null, null);

        Assert.Equal("google", settings.Current.Engine);
        Assert.Equal("EN-US", settings.Current.Lang);
        Assert.Equal("db-google-key", settings.Current.GoogleApiKey);
        Assert.Equal("env-deepl-key", settings.Current.DeeplApiKey);  // 안 건드린 값은 그대로
        Assert.True(settings.Overridden);

        settings.Reset();

        Assert.Equal("deepl", settings.Current.Engine);
        Assert.False(settings.Overridden);
    }

    [Fact]
    public void 빈_칸은_기존_키를_지우지_않는다()
    {
        var (store, settings) = New();
        using var _ = store;

        settings.Save("deepl", null, "typed-key", null, null, null, null, null, null);
        // 모델만 바꾸려고 저장할 때마다 긴 키를 다시 치게 하면 안 된다.
        settings.Save("deepl", null, "", null, null, null, null, null, null);

        Assert.Equal("typed-key", settings.Current.DeeplApiKey);
    }

    [Fact]
    public void 대상_언어는_대문자로_모은다()
    {
        var (store, settings) = New();
        using var _ = store;

        // 캐시 키에 이 표기가 그대로 들어간다 — 기기(KO)와 갈리면 같은 줄을 두 번 번역한다.
        settings.Save(null, "ko", null, null, null, null, null, null, null);

        Assert.Equal("KO", settings.Current.Lang);
        Assert.Equal("KO", TranslationOptions.NormalizeLang(" ko "));
        Assert.Equal("KO", TranslationOptions.NormalizeLang(null));
    }

    [Fact]
    public void 엔진을_바꾸면_다음_실행부터_새_서비스가_쓰인다()
    {
        var (store, settings) = New();
        using var _ = store;

        var before = settings.Service;
        settings.Save("google", null, null, "k", null, null, null, null, null);

        // 붙잡아 둔 서비스를 계속 쓰면 화면에서 바꿔도 옛 엔진이 계속 불린다.
        Assert.NotSame(before, settings.Service);
        Assert.Equal("google", settings.Service.EngineId);
    }

    [Fact]
    public void 키가_없으면_엔진을_골라도_꺼진_상태다()
    {
        var (store, settings) = New();
        using var _ = store;

        settings.Save("openrouter", null, null, null, null, null, null, null, null);

        Assert.False(settings.Current.IsEnabled);
        Assert.False(settings.Service.IsEnabled);
    }

    [Fact]
    public void 기본값은_꺼짐이다()
    {
        // 서버가 부팅만으로 외부 번역 API를 두드리기 시작하면 안 된다.
        var fromEnv = TranslationOptions.FromEnvironment();

        Assert.Equal(TranslatorRegistry.None, fromEnv.Engine);
        Assert.False(fromEnv.IsEnabled);
        Assert.Equal("KO", fromEnv.Lang);
    }

    [Fact]
    public void 일괄_작업에_위험한_엔진은_경고를_단다()
    {
        Assert.Contains("일일 한도", (Env with { Engine = "mymemory" }).Warning);
        Assert.Contains("자체 호스팅", (Env with { Engine = "libretranslate" }).Warning);
        Assert.Null((Env with { Engine = "deepl" }).Warning);
    }

    [Fact]
    public void 번역_캐시는_가사_DB의_같은_커넥션을_쓴다()
    {
        var (store, _) = New();
        using var __ = store;
        var cache = new StoreTranslationCache(store);

        Assert.Null(cache.Get("hello", "KO"));
        cache.Set("hello", "KO", "안녕");

        Assert.Equal("안녕", cache.Get("hello", "KO"));
        Assert.Null(cache.Get("hello", "EN-US"));   // 언어가 다르면 다른 줄이다
        Assert.Equal(1, store.TranslationCacheRows());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* 임시 파일 */ }
    }
}
