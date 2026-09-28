using Musebase.Core.Search;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 일괄 작업이 <b>어떤 곡을 돌릴지</b> 고르는 쿼리와 좋아요 동기화.
/// 여기가 틀리면 이미 번역된 곡을 다시 번역해 돈이 그대로 샌다.
/// </summary>
public class BulkTargetTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"musebase-bulk-test-{Guid.NewGuid():N}.db");

    private LyricsStore NewStore() => new(_dbPath);

    private const string Plain = "[00:01.00]hello\n[00:05.00]world\n";

    /// <summary>대상 언어 번역이 실린 LRC.</summary>
    private static string Translated(string lang) =>
        $"[00:01.00]hello\n[00:01.00][tr:{lang}]안녕\n[00:05.00]world\n[00:05.00][tr:{lang}]세상\n";

    private static LyricsEntry Entry(string title, string artist, string lrc) =>
        new() { Title = title, Artist = artist, Lrc = lrc, Service = "LRCLIB", Origin = LyricsEntry.OriginProvider };

    [Fact]
    public void 대상_언어_판정은_쉼표_경계를_지킨다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);                  // 번역 없음
        store.Upsert(Entry("Time", "Pink Floyd", Translated("ko")), "pc", out _); // 한국어 있음
        store.Upsert(Entry("Money", "Pink Floyd", Translated("kok")), "pc", out _);

        var targets = store.BulkTargets(BulkScope.All, withoutLang: "ko", withoutMeaning: false, limit: 0);

        // "kok"(콘칸어)은 "ko"가 아니다 — LIKE '%ko%'로 걸렀다면 이 곡이 빠졌을 것이다.
        Assert.Equal(["Kids", "Money"], targets.Select(t => t.Title).OrderBy(t => t).ToArray());
        Assert.Equal(2, store.BulkCount(BulkScope.All, "ko", withoutMeaning: false));
    }

    [Fact]
    public void 언어_미상_제공자_번역만_있는_곡은_여전히_대상이다()
    {
        using var store = NewStore();
        // [tr]은 제공자가 준 번역인데 무슨 언어인지 모른다 — langs에 "*"로 들어간다.
        store.Upsert(Entry("Kids", "MGMT", "[00:01.00]hello\n[00:01.00][tr]翻译\n"), "pc", out _);

        // WithoutTranslation(langs = '')을 재사용했다면 이 곡을 "번역 있음"으로 보고 건너뛴다.
        Assert.Empty(store.WithoutTranslation());
        Assert.Single(store.BulkTargets(BulkScope.All, "ko", withoutMeaning: false, limit: 0));
    }

    [Fact]
    public void 좋아요_범위는_해당_서비스의_곡만_고른다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);
        store.Upsert(Entry("Time", "Pink Floyd", Plain), "pc", out _);
        store.Upsert(Entry("Money", "Pink Floyd", Plain), "pc", out _);

        store.SyncLoved([("Kids", "MGMT")]);
        store.SyncSpotifySaved([new SpotifySavedTrack("Time", "Pink Floyd", "spotify:track:t")]);

        Assert.Equal(["Kids"], Titles(store, BulkScope.LovedLastFm));
        Assert.Equal(["Time"], Titles(store, BulkScope.LovedSpotify));
        Assert.Equal(["Kids", "Time"], Titles(store, BulkScope.LovedAny).OrderBy(t => t).ToArray());
        Assert.Equal(3, store.BulkCount(BulkScope.All, null, false));
    }

    [Fact]
    public void Spotify_동기화는_저쪽에서_뺀_곡을_내리고_트랙_URI를_채운다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);
        store.Upsert(Entry("Time", "Pink Floyd", Plain), "pc", out _);

        var first = store.SyncSpotifySaved([
            new SpotifySavedTrack("Kids", "MGMT", "spotify:track:kids"),
            new SpotifySavedTrack("Time", "Pink Floyd", "spotify:track:time"),
        ]);
        Assert.Equal((2, 2), first);

        // 곡마다 /search를 부르지 않게 URI도 함께 적어 둔다.
        var key = LyricsCacheStore.MakeKey("Kids", "MGMT");
        Assert.Equal("spotify:track:kids", store.GetSongLinks(key).SpotifyUri);

        // 두 번째 동기화에서 Kids가 빠졌다 — 여기 남아 있으면 안 된다.
        var second = store.SyncSpotifySaved([new SpotifySavedTrack("Time", "Pink Floyd", "spotify:track:time")]);
        Assert.Equal((1, 1), second);
        Assert.Equal(["Time"], Titles(store, BulkScope.LovedSpotify));
    }

    [Fact]
    public void 서버에_없는_곡은_동기화에서_그냥_건너뛴다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);

        var (matched, total) = store.SyncSpotifySaved([
            new SpotifySavedTrack("Kids", "MGMT", "spotify:track:kids"),
            new SpotifySavedTrack("아직 없는 곡", "누군가", "spotify:track:x"),
        ]);

        Assert.Equal(1, matched);
        Assert.Equal(2, total);   // 사람에게는 "2곡 중 1곡을 맞췄다"로 보여야 한다
    }

    [Fact]
    public void 표기가_달라도_느슨한_키로_맞춘다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);

        // Windows SMTC는 아티스트 뒤에 앨범명을 붙여 보고한다 — 조회와 같은 규칙으로 붙어야 한다.
        var (matched, _) = store.SyncSpotifySaved([
            new SpotifySavedTrack("Kids", "MGMT — Oracular Spectacular", "spotify:track:kids"),
        ]);

        Assert.Equal(1, matched);
    }

    [Fact]
    public void 의미가_없는_곡만_고를_수_있다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);
        store.Upsert(Entry("Time", "Pink Floyd", Plain), "pc", out _);

        store.UpsertMeaning(new MeaningEntry
        {
            Key = LyricsCacheStore.MakeKey("Kids", "MGMT"),
            Title = "Kids", Artist = "MGMT", Lang = "ko", Status = "ok", Summary = "어린 시절에 대한 노래",
        });

        Assert.Equal(["Time"], Titles(store, BulkScope.All, withoutMeaning: true));
        Assert.Equal(1, store.BulkCount(BulkScope.All, null, withoutMeaning: true));
    }

    [Fact]
    public void 상한을_주면_그만큼만_돌려준다()
    {
        using var store = NewStore();
        for (var i = 0; i < 5; i++) store.Upsert(Entry($"곡 {i}", "가수", Plain), "pc", out _);

        Assert.Equal(2, store.BulkTargets(BulkScope.Recent, null, false, limit: 2).Count);
        Assert.Equal(5, store.BulkCount(BulkScope.Recent, null, false));  // 건수는 상한과 무관하다
    }

    [Fact]
    public void 알_수_없는_범위는_받지_않는다()
    {
        Assert.True(BulkScope.IsKnown(BulkScope.LovedAny));
        Assert.False(BulkScope.IsKnown("전체;DROP TABLE lyrics"));
        Assert.False(BulkScope.IsKnown(null));
    }

    [Fact]
    public void 옛_버전_DB를_열어도_행이_보존된다()
    {
        // user_version 9까지만 아는 바이너리가 만든 DB를 다음 버전이 열는 상황.
        using (var old = NewStore())
        {
            old.Upsert(Entry("Kids", "MGMT", Plain), "pc", out _);
            old.SyncLoved([("Kids", "MGMT")]);
        }

        using var store = NewStore();   // 마이그레이션이 한 번 더 돈다(컬럼 추가뿐이라 안전하다)
        Assert.Equal(1, store.Stats().Songs);
        Assert.Equal(["Kids"], Titles(store, BulkScope.LovedLastFm));
        Assert.Empty(Titles(store, BulkScope.LovedSpotify));   // 아직 동기화하지 않았다
    }

    private static string[] Titles(LyricsStore store, string scope, bool withoutMeaning = false) =>
        store.BulkTargets(scope, null, withoutMeaning, limit: 0).Select(t => t.Title).ToArray();

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* 임시 파일 */ }
    }
}
