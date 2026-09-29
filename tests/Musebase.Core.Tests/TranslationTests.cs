using Musebase.Core;
using Musebase.Core.Translation;
using Xunit;

namespace Musebase.Core.Tests;

public class TranslationServiceTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public int CallCount;
        public List<string> LastTexts = [];

        public Task<IReadOnlyList<string?>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
        {
            CallCount++;
            LastTexts = texts.ToList();
            return Task.FromResult<IReadOnlyList<string?>>(
                texts.Select(t => (string?)$"{targetLang}:{t}").ToList());
        }
    }

    private static Lyrics Make(params string[] contents) =>
        new(contents.Select((c, i) => new LyricsLine(c, i * 5.0)));

    [Fact]
    public async Task Ensure_TranslatesMissingLines_AndDisplayPrefersTarget()
    {
        var lyrics = Make("hello", "world");
        lyrics.Lines[0].Attachments[LineAttachments.TranslationTag()] = "제공자 번역"; // "tr"

        var service = new LyricsTranslationService(new FakeTranslator());
        var changed = await service.EnsureTranslatedAsync(lyrics, "KO");

        Assert.Equal(2, changed); // tr:ko는 두 라인 모두 없었음
        // 표시 체인: tr:ko 우선, 제공자 tr 폴백
        Assert.Equal("KO:hello", lyrics.Lines[0].Attachments.Translation("ko"));
        Assert.Equal("KO:world", lyrics.Lines[1].Attachments.Translation("ko"));
        // 제공자 번역 보존
        Assert.Equal("제공자 번역", lyrics.Lines[0].Attachments[LineAttachments.TranslationTag()]);
    }

    [Fact]
    public async Task Ensure_SkipsEmptyAndAlreadyTranslated()
    {
        var lyrics = Make("hello", "", "world");
        lyrics.Lines[2].Attachments[LineAttachments.TranslationTag("ko")] = "이미 있음";

        var translator = new FakeTranslator();
        var service = new LyricsTranslationService(translator);
        await service.EnsureTranslatedAsync(lyrics, "KO");

        Assert.Equal(["hello"], translator.LastTexts); // 빈 라인·기번역 제외
    }

    [Fact]
    public async Task CacheOnly_AppliesCache_ButNeverCallsApi()
    {
        var cache = new InMemoryTranslationCache();
        cache.Set("hello", "KO", "안녕");

        var translator = new FakeTranslator();
        var service = new LyricsTranslationService(translator, cache) { CacheOnly = true };
        var lyrics = Make("hello", "world");
        var changed = await service.EnsureTranslatedAsync(lyrics, "KO");

        Assert.Equal(0, translator.CallCount);        // API 호출 0 = 유료 사용량 0
        Assert.Equal(1, changed);                     // 캐시된 줄만 채움
        Assert.Equal("안녕", lyrics.Lines[0].Attachments.Translation("ko"));
        Assert.Null(lyrics.Lines[1].Attachments.Translation("ko")); // 미스는 번역하지 않음
        Assert.True(service.IsEnabled);               // 엔진/키 설정은 그대로(다시 켜면 즉시 동작)
    }

    [Fact]
    public async Task Ensure_UsesCache_NoTranslatorCallOnSecondSong()
    {
        var cache = new InMemoryTranslationCache();
        var translator = new FakeTranslator();
        var service = new LyricsTranslationService(translator, cache);

        await service.EnsureTranslatedAsync(Make("같은 가사"), "KO");
        Assert.Equal(1, translator.CallCount);

        var second = Make("같은 가사");
        var changed = await service.EnsureTranslatedAsync(second, "KO");

        Assert.Equal(1, translator.CallCount); // 캐시 히트 — 호출 없음
        Assert.Equal(1, changed);
        Assert.Equal("KO:같은 가사", second.Lines[0].Attachments.Translation("ko"));
    }

    [Fact]
    public async Task Ensure_DeduplicatesRepeatedLines()
    {
        var lyrics = Make("후렴", "verse", "후렴", "후렴");
        var translator = new FakeTranslator();
        var service = new LyricsTranslationService(translator);

        var changed = await service.EnsureTranslatedAsync(lyrics, "KO");

        Assert.Equal(["후렴", "verse"], translator.LastTexts); // 중복 1회만 요청
        Assert.Equal(4, changed); // 반영은 4라인 전부
        Assert.All(lyrics.Lines.Where(l => l.Content == "후렴"),
            l => Assert.Equal("KO:후렴", l.Attachments.Translation("ko")));
    }

    [Fact]
    public async Task Ensure_NoTranslator_DoesNothing()
    {
        var lyrics = Make("hello");
        var service = new LyricsTranslationService(null);

        Assert.False(service.IsEnabled);
        Assert.Equal(0, await service.EnsureTranslatedAsync(lyrics, "KO"));
        Assert.Null(lyrics.Lines[0].Attachments.Translation("ko"));
    }

    [Fact]
    public void SqliteCache_PersistsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lyricsx_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var cache = new SqliteTranslationCache(path))
            {
                cache.Set("hello", "KO", "안녕");
                Assert.Equal("안녕", cache.Get("hello", "KO"));
                Assert.Null(cache.Get("hello", "JA"));
            }
            using (var reopened = new SqliteTranslationCache(path))
            {
                Assert.Equal("안녕", reopened.Get("hello", "KO")); // 영속성
            }
        }
        finally
        {
            SqliteTranslationCache.ClearPools();
            File.Delete(path);
        }
    }
}

/// <summary>CompositeTranslator 폴백 체인(ADR-0002) — 주 엔진 실패 시 다음으로.</summary>
public class CompositeTranslatorTests
{
    private sealed class ThrowingTranslator(Exception ex) : ITranslator
    {
        public Task<IReadOnlyList<string?>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default) => throw ex;
    }

    private sealed class EchoTranslator(string prefix) : ITranslator
    {
        public int CallCount;
        public Task<IReadOnlyList<string?>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<string?>>(texts.Select(t => (string?)$"{prefix}:{t}").ToList());
        }
    }

    [Fact]
    public async Task PrimaryQuotaFailure_FallsBackToSecondary_AndReportsQuota()
    {
        var quota = new HttpRequestException("quota", null, (System.Net.HttpStatusCode)456);
        TranslatorFailure? reported = null;
        var fallback = new EchoTranslator("libre");
        var composite = new CompositeTranslator(
            [("deepl", new ThrowingTranslator(quota)), ("libretranslate", fallback)],
            f => reported = f);

        var result = await composite.TranslateAsync(["a", "b"], "KO");

        Assert.Equal(["libre:a", "libre:b"], result);
        Assert.Equal(1, fallback.CallCount);
        Assert.NotNull(reported);
        Assert.Equal("deepl", reported!.EngineId);
        Assert.Equal(456, reported.HttpStatus);
        Assert.Equal(TranslatorFailureKind.Quota, reported.Kind);
    }

    [Fact]
    public async Task AllFail_ReturnsNulls_WithoutThrowing()
    {
        var boom = new HttpRequestException("nope", null, (System.Net.HttpStatusCode)403);
        var reports = new List<TranslatorFailure>();
        var composite = new CompositeTranslator(
            [("deepl", new ThrowingTranslator(boom))], reports.Add);

        var result = await composite.TranslateAsync(["x"], "KO");

        Assert.Single(result);
        Assert.Null(result[0]);
        Assert.Equal(TranslatorFailureKind.Auth, Assert.Single(reports).Kind);
    }

    [Fact]
    public async Task PrimarySucceeds_SecondaryNotCalled()
    {
        var primary = new EchoTranslator("deepl");
        var secondary = new EchoTranslator("libre");
        var composite = new CompositeTranslator([("deepl", primary), ("libretranslate", secondary)]);

        var result = await composite.TranslateAsync(["a"], "KO");

        Assert.Equal(["deepl:a"], result);
        Assert.Equal(0, secondary.CallCount);
    }

    /// <summary>줄 일부만 돌려주는 번역기 — LLM이 묶음을 버릴 때 실제로 이렇게 나온다.</summary>
    private sealed class PartialTranslator(params int[] fillIndexes) : ITranslator
    {
        public List<string> Received { get; } = [];

        public Task<IReadOnlyList<string?>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
        {
            Received.AddRange(texts);
            return Task.FromResult<IReadOnlyList<string?>>(
                texts.Select((t, i) => fillIndexes.Contains(i) ? $"ok:{t}" : null).ToList());
        }
    }

    /// <summary>
    /// 부분 실패의 핵심 규칙 — <b>못 채운 줄만</b> 다음 엔진으로 간다. 이미 채운 줄까지 넘기면
    /// 같은 문자를 두 번 과금하고, 반대로 안 넘기면 구멍이 남는다.
    /// </summary>
    [Fact]
    public async Task 부분_실패는_미채운_줄만_다음_엔진으로_넘긴다()
    {
        var primary = new PartialTranslator(0, 2);        // a와 c만 채운다
        var secondary = new PartialTranslator(0, 1);      // 넘어온 것은 전부 채운다
        var composite = new CompositeTranslator([("deepl", primary), ("google", secondary)]);

        var result = await composite.TranslateAsync(["a", "b", "c", "d"], "KO");

        Assert.Equal(["ok:a", "ok:b", "ok:c", "ok:d"], result);
        Assert.Equal(["a", "b", "c", "d"], primary.Received);
        Assert.Equal(["b", "d"], secondary.Received);     // 이미 채운 a·c는 다시 보내지 않는다
    }

    [Fact]
    public async Task 빈_문자열은_채운_것으로_치지_않는다()
    {
        // 번역기가 ""를 돌려주는 것은 "이 줄은 번역할 게 없다"가 아니라 대개 실패다.
        var primary = new PartialTranslator();            // 전부 null
        var secondary = new EchoTranslator("google");
        var composite = new CompositeTranslator([("deepl", primary), ("google", secondary)]);

        var result = await composite.TranslateAsync(["a"], "KO");

        Assert.Equal(["google:a"], result);
    }

    [Fact]
    public async Task 건너뛰기_술어가_참이면_그_엔진은_아예_부르지_않는다()
    {
        // 한도를 맞은 엔진에 곡마다 다시 부딪히지 않게 하는 장치.
        var skipped = new EchoTranslator("deepl");
        var next = new EchoTranslator("google");
        var composite = new CompositeTranslator(
            [("deepl", skipped), ("google", next)],
            new CompositeTranslatorHooks(Skip: id => id == "deepl"));

        var result = await composite.TranslateAsync(["a"], "KO");

        Assert.Equal(["google:a"], result);
        Assert.Equal(0, skipped.CallCount);
    }

    [Fact]
    public async Task 누가_몇_줄_몇_자를_채웠는지_보고한다()
    {
        // 엔진별 기여는 이 루프 안에서만 알 수 있다 — 사용량 미터와 작업 요약이 여기 걸려 있다.
        var filled = new List<(string Engine, int Lines, int Chars)>();
        var composite = new CompositeTranslator(
            [("deepl", new PartialTranslator(0)), ("google", new EchoTranslator("google"))],
            new CompositeTranslatorHooks(OnFilled: (e, l, c) => filled.Add((e, l, c))));

        await composite.TranslateAsync(["abc", "de"], "KO");

        Assert.Equal(("deepl", 1, 3), filled[0]);   // "abc"만 채웠다 → 3자
        Assert.Equal(("google", 1, 2), filled[1]);  // 남은 "de" → 2자
    }

    [Fact]
    public async Task 한_줄도_못_채운_엔진은_채움_보고를_하지_않는다()
    {
        var filled = new List<string>();
        var composite = new CompositeTranslator(
            [("deepl", new PartialTranslator()), ("google", new EchoTranslator("google"))],
            new CompositeTranslatorHooks(OnFilled: (e, _, _) => filled.Add(e)));

        await composite.TranslateAsync(["a"], "KO");

        Assert.Equal(["google"], filled);
    }
}
