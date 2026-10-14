using System.Runtime.CompilerServices;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 연주곡 — 제목 추정(검색·서버 키 매칭)과 사람이 하는 표시(앱·관리 화면 → 서버 → 모든 기기).
/// 실제 사례: "Natural wonders every child should know (Instrumental version)"에 서버가 느슨한 키로
/// 원곡 가사를 내려 줬다.
/// </summary>
public class InstrumentalTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"musebase-instr-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 정리 실패는 무시 */ }
    }

    // ---- 제목 추정 ----

    [Theory]
    [InlineData("Natural wonders every child should know (Instrumental version)")]
    [InlineData("Song - Instrumental")]
    [InlineData("Song (Instrumentals)")]
    [InlineData("Song (Inst.)")]
    [InlineData("Song [Inst]")]
    [InlineData("Song - Inst")]
    [InlineData("Song (Inst. Ver.)")]
    [InlineData("Song (Off Vocal)")]
    [InlineData("Song (off-vocal ver.)")]
    [InlineData("歌 (伴奏)")]
    [InlineData("노래 (연주곡)")]
    public void 연주곡_표기를_알아본다(string title) => Assert.True(InstrumentalTitle.Looks(title));

    [Theory]
    [InlineData("Instinct")]
    [InlineData("Install Wizard")]
    [InlineData("Song (Karaoke Version)")]   // 반주로 따라 부를 때 가사가 필요하다
    [InlineData("Song (Live)")]
    [InlineData("")]
    [InlineData(null)]
    public void 연주곡이_아닌_제목은_잡지_않는다(string? title) => Assert.False(InstrumentalTitle.Looks(title));

    // ---- 서버 ----

    private static LyricsEntry Entry(string title, string artist = "Pet Shop Boys") => new()
    {
        Title = title, Artist = artist, Lrc = "[00:01.00]words\n[00:02.00]more words\n", Service = "NetEase",
    };

    [Fact]
    public void 연주곡_제목은_느슨한_키로_원곡_가사를_받지_않는다()
    {
        using var store = new LyricsStore(_dbPath);
        store.Upsert(Entry("Natural wonders every child should know"), "pc", out _);

        Assert.Null(store.Get("Natural wonders every child should know (Instrumental version)", "Pet Shop Boys"));
        Assert.NotNull(store.Get("Natural wonders every child should know", "Pet Shop Boys"));
    }

    [Fact]
    public void 연주곡_가사를_올려도_원곡_행을_덮지_않고_원곡_질의도_연주곡_행에_맞지_않는다()
    {
        using var store = new LyricsStore(_dbPath);
        store.Upsert(Entry("Song"), "pc", out _);
        var saved = store.Upsert(Entry("Song (Instrumental)"), "pc", out _);

        Assert.Equal(LyricsStore.ExactKey("Song (Instrumental)", "Pet Shop Boys"), saved!.Key);
        Assert.Equal("Song", store.Get("Song", "Pet Shop Boys")!.Title);
        Assert.Null(store.Get("Song (Remastered)", "Other Artist"));
    }

    [Fact]
    public void 연주곡으로_표시하면_그_곡만_지우고_조회를_막는다()
    {
        using var store = new LyricsStore(_dbPath);
        store.Upsert(Entry("Song"), "pc", out _);
        store.Upsert(Entry("Song (Instrumental)"), "pc", out _);
        store.Upsert(Entry("Interlude"), "pc", out _);

        store.AddInstrumental("Interlude", "Pet Shop Boys", "s26");

        Assert.True(store.IsInstrumental("Interlude", "pet shop boys"));
        Assert.Null(store.GetByKey(LyricsStore.ExactKey("Interlude", "Pet Shop Boys")));
        Assert.NotNull(store.Get("Song", "Pet Shop Boys"));                 // 다른 곡은 그대로
        Assert.Equal("s26", Assert.Single(store.Instrumentals()).AddedBy);

        store.RemoveInstrumental(LyricsStore.ExactKey("Interlude", "Pet Shop Boys"));
        Assert.False(store.IsInstrumental("Interlude", "Pet Shop Boys"));
    }

    // ---- 앱(엔진) ----

    private const string Vocal = "[ti:Song]\n[00:01.00]hello\n[00:05.00]world";
    private const string InstrumentalLrc = "[ti:Song (Instrumental)]\n[00:01.00]纯音乐，请欣赏";

    [Fact]
    public async Task 연주곡_제목에는_원곡_가사_후보를_붙이지_않고_연주곡으로_알린다()
    {
        var remote = new FakeRemote();
        using var coordinator = NewCoordinator(remote, "Song (Instrumental)", new StubProvider(Vocal));
        var statuses = new List<LyricsStatusKind>();
        coordinator.StatusChanged += s => statuses.Add(s.Kind);
        coordinator.Start();

        await WaitAsync(() => statuses.Contains(LyricsStatusKind.Instrumental), "연주곡 상태");

        Assert.Null(coordinator.CurrentLyrics);
        Assert.Empty(remote.Uploads);
        Assert.Empty(coordinator.InstrumentalTrackKeys); // 추정일 뿐 — 표시(기억)하지 않는다
    }

    [Fact]
    public async Task 연주곡_제목이어도_후보가_연주곡_판이면_받는다()
    {
        var remote = new FakeRemote();
        using var coordinator = NewCoordinator(remote, "Song (Instrumental)", new StubProvider(InstrumentalLrc));
        coordinator.Start();

        await WaitAsync(() => coordinator.CurrentLyrics is not null, "가사 채택");
    }

    [Fact]
    public async Task 연주곡으로_표시하면_가사를_지우고_서버에_알리고_기억한다()
    {
        var remote = new FakeRemote();
        using var coordinator = NewCoordinator(remote, "Interlude", new StubProvider(Vocal));
        var changed = 0;
        coordinator.InstrumentalTracksChanged += () => changed++;
        coordinator.Start();
        await WaitAsync(() => coordinator.CurrentLyrics is not null, "가사 채택");

        coordinator.SetCurrentInstrumental(true);

        Assert.Null(coordinator.CurrentLyrics);
        Assert.Equal(LyricsStatusKind.Instrumental, coordinator.CurrentStatus?.Kind);
        Assert.True(coordinator.IsCurrentInstrumental);
        Assert.Equal(1, changed);
        Assert.Equal(("Interlude", true), Assert.Single(remote.Marks));
    }

    [Fact]
    public async Task 표시된_곡은_다시_찾지_않고_해제하면_다시_찾는다()
    {
        var remote = new FakeRemote();
        var provider = new StubProvider(Vocal);
        using var coordinator = NewCoordinator(remote, "Interlude", provider);
        coordinator.InstrumentalTrackKeys.Add(LyricsCacheStore.MakeKey("Interlude", "Artist"));
        coordinator.Start();
        await Task.Delay(100);

        Assert.Equal(0, remote.Lookups);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(LyricsStatusKind.Instrumental, coordinator.CurrentStatus?.Kind);

        coordinator.SetCurrentInstrumental(false);
        await WaitAsync(() => coordinator.CurrentLyrics is not null, "다시 찾기");
        Assert.Equal(("Interlude", false), Assert.Single(remote.Marks));
    }

    [Fact]
    public async Task 서버가_연주곡이라고_하면_찾지_않고_이_기기에도_기억한다()
    {
        var remote = new FakeRemote { Instrumental = true };
        var provider = new StubProvider(Vocal);
        using var coordinator = NewCoordinator(remote, "Interlude", provider);
        coordinator.Start();

        await WaitAsync(() => coordinator.CurrentStatus?.Kind == LyricsStatusKind.Instrumental, "연주곡 상태");
        Assert.Equal(0, provider.Calls);
        Assert.True(coordinator.IsCurrentInstrumental);
    }

    // ---- 배선 ----

    private static async Task WaitAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (done()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"{what}가 일어나지 않았습니다.");
    }

    private static LyricsCoordinator NewCoordinator(FakeRemote remote, string title, ILyricsProvider provider)
    {
        var source = new FakeSource { CurrentTrack = new TrackInfo(title, "Artist", "", null, "TestPlayer.exe") };
        return new LyricsCoordinator(source, new InlineDispatcher(), new LyricsSearchService(provider))
        {
            RemoteCache = remote,
            Translation = new LyricsTranslationService(null, new InMemoryTranslationCache()),
        };
    }

    private sealed class FakeRemote : IRemoteLyricsCache
    {
        public bool Instrumental { get; init; }
        public int Lookups;
        public List<(string Title, string Artist)> Uploads { get; } = [];
        public List<(string Title, bool On)> Marks { get; } = [];

        public Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult(Instrumental ? RemoteLyricsResult.Instrumental : RemoteLyricsResult.Miss);
        }

        public Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default)
        {
            lock (Uploads) Uploads.Add((title, artist));
            return Task.CompletedTask;
        }

        public Task<bool> SetInstrumentalAsync(string title, string artist, bool instrumental, CancellationToken ct = default)
        {
            lock (Marks) Marks.Add((title, instrumental));
            return Task.FromResult(true);
        }

        public Task<SongMeaningView?> GetMeaningAsync(string title, string artist, CancellationToken ct = default) =>
            Task.FromResult<SongMeaningView?>(null);

        public Task<MeaningRequestResult> RequestMeaningAsync(
            string title, string artist, bool force = false, CancellationToken ct = default) =>
            Task.FromResult(MeaningRequestResult.Of(MeaningRequestStatus.Unavailable));

        public Task<SongExtrasResult> GetExtrasAsync(string title, string artist, CancellationToken ct = default) =>
            Task.FromResult(SongExtrasResult.NotFound);

        public Task<SongExtras?> RefreshCoverAsync(string title, string artist, CancellationToken ct = default) =>
            Task.FromResult<SongExtras?>(null);

        public Task<SongExtras?> SetLovedAsync(string title, string artist, bool loved, CancellationToken ct = default) =>
            Task.FromResult<SongExtras?>(null);
    }

    private sealed class StubProvider(string lrc) : ILyricsProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string ServiceName => "LRCLIB";

        public async IAsyncEnumerable<Lyrics> GetLyricsAsync(
            LyricsSearchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            var lyrics = Lyrics.Parse(lrc)!;
            lyrics.Metadata.ServiceName = ServiceName;
            lyrics.Metadata.Request = request;
            yield return lyrics;
        }
    }

    private sealed class FakeSource : INowPlayingSource
    {
        public TrackInfo? CurrentTrack { get; set; }
        public bool IsPlaying { get; set; }
#pragma warning disable CS0067 // 이 테스트는 트랙·재생 변경을 일으키지 않는다
        public event Action<TrackInfo?>? TrackChanged;
        public event Action<bool>? IsPlayingChanged;
#pragma warning restore CS0067
        public TimeSpan? GetEstimatedPosition() => TimeSpan.Zero;
        public PlaybackControls GetControls() => PlaybackControls.None;
        public Task<bool> TogglePlayPauseAsync() => Task.FromResult(true);
        public Task<bool> SkipNextAsync() => Task.FromResult(true);
        public Task<bool> SkipPreviousAsync() => Task.FromResult(true);
    }

    private sealed class InlineDispatcher : IEngineDispatcher
    {
        public void Post(Action action) => action();
        public IEngineTimer CreateTimer(TimeSpan interval, Action tick) => new NoopTimer();
        private sealed class NoopTimer : IEngineTimer { public void Start() { } public void Stop() { } }
    }
}
