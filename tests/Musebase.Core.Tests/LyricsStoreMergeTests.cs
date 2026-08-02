using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 서버 저장소의 **저장 쪽 키 매칭**. 조회(<c>Get</c>)는 느슨한 키까지 보는데 저장이 정확 키만 보면
/// "조회로는 맞는데 올리면 새 행이 생기는" 비대칭이 생긴다 — 기기마다 메타데이터 표기가 다르므로
/// (Windows SMTC는 아티스트에 앨범명을 붙여 보고한다) 실제로 자주 일어난다.
/// SQLite 파일을 임시 폴더에 만들어 돌린다(순수 함수가 아니라 저장 경로 자체를 봐야 한다).
/// </summary>
public class LyricsStoreMergeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"musebase-store-test-{Guid.NewGuid():N}.db");

    private const string Plain = "[00:01.00]hello\n[00:05.00]world\n";
    private const string Translated =
        "[00:01.00]hello\n[00:01.00][tr:ko]안녕\n[00:05.00]world\n[00:05.00][tr:ko]세상\n";

    private LyricsStore NewStore() => new(_dbPath);

    private static LyricsEntry Entry(string title, string artist, string lrc, string origin = LyricsEntry.OriginProvider) =>
        new() { Title = title, Artist = artist, Lrc = lrc, Service = "LRCLIB", Origin = origin };

    [Fact]
    public void 아티스트에_앨범명이_붙은_표기로_올려도_기존_행을_갱신한다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "안드로이드", out _);

        // Windows SMTC 표기 — 아티스트 뒤에 앨범명이 붙는다.
        var saved = store.Upsert(
            Entry("Kids", "MGMT — Oracular Spectacular", Translated), "윈도우PC", out var rejection);

        Assert.Null(rejection);
        Assert.NotNull(saved);
        Assert.Equal(1, store.Stats().Songs);          // 중복 행이 생기지 않는다
        Assert.Equal(1, store.Stats().WithTranslation); // 번역이 기존 행에 실린다
        Assert.Equal(2, saved!.Revision);               // 새 행이 아니라 갱신(revision 증가)
    }

    [Fact]
    public void 합쳐_넣을_때_제목과_아티스트는_먼저_저장된_표기를_유지한다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "안드로이드", out _);
        store.Upsert(Entry("Kids", "MGMT — Oracular Spectacular", Translated), "윈도우PC", out _);

        // 표기를 바꿔 버리면 원래 기기(정확 키 "kids|mgmt")의 조회가 깨진다.
        var byOriginal = store.Get("Kids", "MGMT");
        Assert.NotNull(byOriginal);
        Assert.Equal("MGMT", byOriginal!.Artist);
        Assert.Equal(LyricsEntry.MatchExact, byOriginal.Match);
        Assert.Contains("[tr:ko]", byOriginal.Lrc);

        // 나중에 올린 기기도 계속 맞는다(느슨한 키).
        var byNoisy = store.Get("Kids", "MGMT — Oracular Spectacular");
        Assert.NotNull(byNoisy);
        Assert.Equal(byOriginal.Key, byNoisy!.Key);
    }

    [Fact]
    public void 합쳐_넣는_경우에도_사용자_편집본은_보호된다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Translated, LyricsEntry.OriginUser), "윈도우PC", out _);

        var saved = store.Upsert(
            Entry("Kids", "MGMT — Oracular Spectacular", Plain), "안드로이드", out var rejection);

        Assert.Null(saved);
        Assert.Equal(PutRejected.UserEditProtected, rejection);
        Assert.Equal(1, store.Stats().Songs);
    }

    /// <summary>
    /// Spotify Android는 아티스트 뒤에 "• 스마트셔플 추천"을 붙인다(실측). 이걸 흡수하지 않으면
    /// 같은 곡이 기기마다 다른 키로 갈려 서로의 업로드를 받지 못한다.
    /// </summary>
    [Fact]
    public void 불릿_꼬리표가_붙은_표기도_기존_행에_합쳐진다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Go!", "M83", Plain), "윈도우PC", out _);

        var saved = store.Upsert(Entry("Go!", "M83 • 스마트셔플 추천", Translated), "안드로이드", out var rejection);

        Assert.Null(rejection);
        Assert.Equal(1, store.Stats().Songs);
        Assert.Equal(2, saved!.Revision);
        Assert.Equal("M83", store.Get("Go!", "M83")!.Artist); // 먼저 저장된 표기 유지
    }

    [Fact]
    public void 서로_다른_곡은_합쳐지지_않는다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "안드로이드", out _);
        store.Upsert(Entry("Time to Pretend", "MGMT", Plain), "안드로이드", out _);

        Assert.Equal(2, store.Stats().Songs);
    }

    /// <summary>
    /// 의미는 가사와 **같은 해석기**로 찾아야 한다 — 가사가 느슨한 키로 맞는 곡은
    /// 의미도 같이 맞지 않으면 앱에서 가사는 뜨는데 의미만 비는 일이 생긴다.
    /// </summary>
    [Fact]
    public void 의미는_가사와_같은_키_해석기로_찾는다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "윈도우PC", out _);

        store.UpsertMeaning(new MeaningEntry
        {
            Key = "kids|mgmt",
            Title = "Kids",
            Artist = "MGMT",
            Summary = "성장의 불안에 대한 곡이다.",
            Lang = "ko",
            Sources = "[]",
            Status = MeaningEntry.StatusOk,
            UpdatedAt = "2026-08-01T00:00:00Z",
        });

        // 정확 키
        Assert.NotNull(store.GetMeaning("Kids", "MGMT"));
        // 꼬리표가 붙은 표기(느슨한 키)로도 같은 의미가 나와야 한다
        Assert.NotNull(store.GetMeaning("Kids", "MGMT • 스마트셔플 추천"));
        Assert.NotNull(store.GetMeaning("Kids", "MGMT — Oracular Spectacular"));
        // 다른 곡은 없다
        Assert.Null(store.GetMeaning("Time to Pretend", "MGMT"));
    }

    [Fact]
    public void 이미_시도한_곡은_백필_대상에서_빠진다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "윈도우PC", out _);
        store.Upsert(Entry("Go!", "M83", Plain), "윈도우PC", out _);

        Assert.Equal(2, store.SongsWithoutMeaning(50).Count);

        // 자료를 못 찾은 곡도 행으로 남는다 — 백필을 다시 눌러도 무한 재시도하지 않는다.
        store.UpsertMeaning(new MeaningEntry
        {
            Key = "kids|mgmt",
            Title = "Kids",
            Artist = "MGMT",
            Lang = "ko",
            Sources = "[]",
            Status = MeaningEntry.StatusNoSource,
            UpdatedAt = "2026-08-01T00:00:00Z",
        });

        var remaining = store.SongsWithoutMeaning(50);
        Assert.Single(remaining);
        Assert.Equal("Go!", remaining[0].Title);

        var (ok, none, failed) = store.MeaningStats();
        Assert.Equal(0, ok);
        Assert.Equal(1, none);
        Assert.Equal(0, failed);
    }

    // ---- 관리자 화면이 기대는 조회 ----

    [Fact]
    public void 의미_필터가_상태별로_갈라_준다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "윈도우PC", out _);
        store.Upsert(Entry("Go!", "M83", Plain), "윈도우PC", out _);

        store.UpsertMeaning(new MeaningEntry
        {
            Key = "kids|mgmt", Title = "Kids", Artist = "MGMT", Lang = "ko", Sources = "[]",
            Summary = "성장의 불안에 대한 곡이다.",
            Status = MeaningEntry.StatusOk, UpdatedAt = "2026-08-01T00:00:00Z",
        });

        Assert.Equal(2, store.Search(null).Count);

        var withMeaning = store.Search(null, meaning: LyricsStore.MeaningFilterOk);
        Assert.Single(withMeaning);
        Assert.Equal("Kids", withMeaning[0].Title);
        Assert.Equal(MeaningEntry.StatusOk, withMeaning[0].MeaningStatus);

        var without = store.Search(null, meaning: LyricsStore.MeaningFilterNone);
        Assert.Single(without);
        Assert.Equal("Go!", without[0].Title);
        Assert.Null(without[0].MeaningStatus);
    }

    [Fact]
    public void 자료를_못_찾은_곡은_의미_있음에_들지_않는다()
    {
        using var store = NewStore();
        store.Upsert(Entry("Kids", "MGMT", Plain), "윈도우PC", out _);
        store.UpsertMeaning(new MeaningEntry
        {
            Key = "kids|mgmt", Title = "Kids", Artist = "MGMT", Lang = "ko", Sources = "[]",
            Status = MeaningEntry.StatusNoSource, UpdatedAt = "2026-08-01T00:00:00Z",
        });

        Assert.Empty(store.Search(null, meaning: LyricsStore.MeaningFilterOk));
        Assert.Single(store.Search(null, meaning: LyricsStore.MeaningFilterNone));
    }

    [Fact]
    public void 미스로_기록된_조회도_나중에_올라온_가사를_찾아낸다()
    {
        using var store = NewStore();
        store.LogLookup("Kids", "MGMT", LyricsEntry.MatchMiss, null, "안드로이드", null);

        // 그때는 없었다.
        Assert.Null(store.RecentLookups(10)[0].Key);

        // 나중에 (표기가 조금 다른 채로) 올라왔다.
        store.Upsert(Entry("Kids", "MGMT — Oracular Spectacular", Plain), "윈도우PC", out _);

        var row = store.RecentLookups(10)[0];
        Assert.NotNull(row.Key);                                   // 이제 곡으로 갈 수 있다
        Assert.Equal(LyricsEntry.MatchMiss, row.Result);           // 기록 자체는 바꾸지 않는다
    }

    [Fact]
    public void 미스_상위도_지금_서버에_있으면_키를_붙인다()
    {
        using var store = NewStore();
        store.LogLookup("Kids", "MGMT", LyricsEntry.MatchMiss, null, "안드로이드", null);
        store.LogLookup("Go!", "M83", LyricsEntry.MatchMiss, null, "안드로이드", null);
        store.Upsert(Entry("Kids", "MGMT", Plain), "윈도우PC", out _);

        var misses = store.TopMisses("2000-01-01T00:00:00Z");
        Assert.Equal("kids|mgmt", misses.Single(m => m.Title == "Kids").Key);
        Assert.Null(misses.Single(m => m.Title == "Go!").Key);     // 정말 없는 곡은 그대로 null
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 정리 실패는 무시 */ }
    }
}
