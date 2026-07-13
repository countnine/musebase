using LyricsX.Core;
using LyricsX.Core.Translation;
using Xunit;

namespace LyricsX.Core.Tests;

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
