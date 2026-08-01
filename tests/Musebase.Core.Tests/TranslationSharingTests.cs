using System.Runtime.CompilerServices;
using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 번역 공유 — 가사 서버의 목적 중 하나는 **한 기기가 번역한 결과를 다른 기기가 다시 번역하지 않는 것**이다.
/// 제공자 검색 직후의 업로드만으로는 부족하다: 저장 당시 번역이 없었던 곡(API 꺼짐·한도 초과)이나
/// 서버에 대상 언어가 없는 곡은 각 기기가 매번 따로 번역하게 된다. 그래서 캐시/서버 히트 뒤
/// **보충 번역이 실제로 채워지면** 로컬 캐시와 서버에 되돌려 저장한다.
/// </summary>
public class TranslationSharingTests
{
    private const string Plain = "[00:01.00]hello\n[00:05.00]world";
    private const string Translated =
        "[00:01.00]hello\n[00:01.00][tr:ko]KO:hello\n[00:05.00]world\n[00:05.00][tr:ko]KO:world";

    [Fact]
    public async Task 서버_가사에_번역이_없으면_번역해서_서버에_되돌린다()
    {
        var remote = new FakeRemoteCache(Plain);
        using var coordinator = NewCoordinator(remote, out var source);
        coordinator.Start();

        var uploaded = await remote.WaitForUploadAsync();

        Assert.Contains("[tr:ko]", uploaded.Lrc);
        Assert.Equal("Song", uploaded.Title);
        Assert.Equal("Artist", uploaded.Artist);
        Assert.Single(remote.Uploads); // 같은 내용을 여러 번 올리지 않는다
        Assert.NotNull(source.CurrentTrack);
    }

    [Fact]
    public async Task 이미_번역된_서버_가사는_다시_올리지_않는다()
    {
        var remote = new FakeRemoteCache(Translated);
        using var coordinator = NewCoordinator(remote, out _);
        coordinator.Start();

        await remote.WaitForLookupAsync();
        await Task.Delay(150); // 뒤늦은 업로드가 없는지 확인할 여유

        Assert.Empty(remote.Uploads);
    }

    [Fact]
    public async Task 번역_없이_저장된_로컬_캐시도_보충_번역_뒤_캐시와_서버에_반영된다()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"musebase-cache-test-{Guid.NewGuid():N}.db");
        try
        {
            using var cache = new LyricsCacheStore(dbPath);
            var untranslated = Lyrics.Parse(Plain)!;
            untranslated.Metadata.ServiceName = "LRCLIB";
            cache.Set("Song", "Artist", untranslated); // 번역 API가 꺼진 채 저장된 상태

            var remote = new FakeRemoteCache(null);
            using (var coordinator = NewCoordinator(remote, out _, cache))
            {
                coordinator.Start();
                await remote.WaitForUploadAsync();
            }

            // 로컬 캐시도 갱신돼 다음 재생부터는 번역을 다시 채울 필요가 없다.
            Assert.Contains("[tr:ko]", cache.Get("Song", "Artist")!.ToString());
            Assert.Equal(0, remote.Lookups); // 캐시 히트라 서버 조회는 하지 않는다
            Assert.Single(remote.Uploads);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* 정리 실패는 무시 */ }
        }
    }

    /// <summary>
    /// 재생 소스는 길이·앨범 같은 메타데이터를 뒤늦게 채워 넣으며 같은 곡을 다시 통지한다
    /// (TrackInfo는 record라 그 필드까지 같아야 같은 값이다). 그때마다 검색을 다시 돌리면
    /// 가사 서버에 같은 요청이 두 번 나간다 — 실제 서버 로그에서 1~8초 간격 중복으로 확인됐다.
    /// </summary>
    [Fact]
    public async Task 길이만_늦게_채워진_같은_곡은_서버에_다시_묻지_않는다()
    {
        var remote = new FakeRemoteCache(null); // 미스 → 제공자 검색으로 흘러간다(빈손)
        using var coordinator = NewCoordinator(remote, out var source);
        coordinator.Start();
        await remote.WaitForLookupAsync();

        source.RaiseTrack(new TrackInfo("Song", "Artist", "", TimeSpan.FromMinutes(3), "TestPlayer.exe"));
        source.RaiseTrack(new TrackInfo("Song", "Artist", "Some Album", TimeSpan.FromMinutes(3), "TestPlayer.exe"));
        await Task.Delay(150);

        Assert.Equal(1, remote.Lookups);
    }

    [Fact]
    public async Task 곡이_실제로_바뀌면_다시_묻는다()
    {
        var remote = new FakeRemoteCache(null);
        using var coordinator = NewCoordinator(remote, out var source);
        coordinator.Start();
        await remote.WaitForLookupAsync();

        source.RaiseTrack(new TrackInfo("Other Song", "Artist", "", null, "TestPlayer.exe"));
        await Task.Delay(150);

        Assert.Equal(2, remote.Lookups);
    }

    /// <summary>
    /// Spotify Connect처럼 두 기기가 같은 곡을 동시에 처리할 때, 서버가 "다른 기기도 방금 물었다"고
    /// 알려 주면 두 번째 기기는 번역을 양보한다 — 저쪽이 올린 번역본을 받아 쓰고 API를 부르지 않는다.
    /// </summary>
    [Fact]
    public async Task 양보_중_서버에_번역본이_올라오면_직접_번역하지_않는다()
    {
        var translator = new FakeTranslator();
        var remote = new FakeRemoteCache(null)
        {
            PendingOnMiss = true,     // 첫 조회: 미스 + 양보 힌트
            ArrivingLrc = Translated, // 재조회부터는 다른 기기가 올린 번역본이 있다
            ArriveAfter = 2,
        };
        using var coordinator = NewCoordinator(
            remote, out _, translator: translator, provider: new StubProvider(Plain));
        coordinator.Start();

        await WaitAsync(() => remote.Lookups >= 2, "양보 재조회");
        await Task.Delay(200);

        Assert.Equal(0, translator.Calls);                       // DeepL을 부르지 않았다
        Assert.Contains("[tr:ko]", coordinator.CurrentLyrics!.ToString()); // 번역은 붙어 있다
        Assert.Empty(remote.Uploads);                            // 받아 쓴 것을 되돌려 올리지도 않는다
    }

    [Fact]
    public async Task 양보해도_번역본이_안_오면_직접_번역한다()
    {
        var translator = new FakeTranslator();
        var remote = new FakeRemoteCache(null) { PendingOnMiss = true }; // 끝까지 미스
        using var coordinator = NewCoordinator(
            remote, out _, translator: translator, provider: new StubProvider(Plain));
        coordinator.Start();

        await WaitAsync(() => translator.Calls > 0, "직접 번역");
        Assert.Contains("[tr:ko]", coordinator.CurrentLyrics!.ToString());
    }

    private static async Task WaitAsync(Func<bool> done, string what, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (done()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"{what}가 일어나지 않았습니다.");
    }

    // ---- 배선 ----

    private static LyricsCoordinator NewCoordinator(
        FakeRemoteCache remote, out FakeSource source,
        LyricsCacheStore? cache = null, FakeTranslator? translator = null, ILyricsProvider? provider = null)
    {
        source = new FakeSource { CurrentTrack = new TrackInfo("Song", "Artist", "", null, "TestPlayer.exe") };
        return new LyricsCoordinator(
            source, new InlineDispatcher(), new LyricsSearchService(provider ?? new EmptyProvider()))
        {
            RemoteCache = remote,
            Cache = cache,
            Translation = new LyricsTranslationService(
                translator ?? new FakeTranslator(), new InMemoryTranslationCache()),
        };
    }

    /// <summary>
    /// 조회는 정해진 LRC(없으면 미스)를 돌려주고, 업로드는 기록만 한다.
    /// <see cref="PendingOnMiss"/>를 켜면 미스에 양보 힌트를 실어 주고, <see cref="ArriveAfter"/>번째
    /// 조회부터 <see cref="ArrivingLrc"/>를 내려 준다(다른 기기가 뒤늦게 올린 상황을 흉내낸다).
    /// </summary>
    private sealed class FakeRemoteCache(string? lrc) : IRemoteLyricsCache
    {
        private readonly object _lock = new();
        public List<(string Title, string Artist, string Lrc)> Uploads { get; } = [];
        public int Lookups { get; private set; }

        public bool PendingOnMiss { get; init; }
        public string? ArrivingLrc { get; init; }
        public int ArriveAfter { get; init; } = int.MaxValue;

        public Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default)
        {
            int n;
            lock (_lock) n = ++Lookups;

            var body = n >= ArriveAfter ? ArrivingLrc ?? lrc : lrc;
            if (body is null)
                return Task.FromResult(PendingOnMiss
                    ? new RemoteLyricsResult(null, true, 1000, [])
                    : RemoteLyricsResult.Miss);

            var lyrics = Lyrics.Parse(body)!;
            lyrics.Metadata.ServiceName = "LRCLIB";
            var langs = body.Contains("[tr:ko]", StringComparison.Ordinal) ? new[] { "ko" } : [];
            return Task.FromResult(new RemoteLyricsResult(lyrics, false, 0, langs));
        }

        public Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default)
        {
            lock (_lock) Uploads.Add((title, artist, lyrics.ToString()));
            return Task.CompletedTask;
        }

        public Task<(string Title, string Artist, string Lrc)> WaitForUploadAsync() =>
            WaitAsync(() => { lock (_lock) return Uploads.Count > 0 ? Uploads[0] : default; },
                v => v.Lrc is not null, "업로드");

        public Task WaitForLookupAsync() =>
            WaitAsync(() => { lock (_lock) return Lookups; }, n => n > 0, "서버 조회");

        private static async Task<T> WaitAsync<T>(Func<T> probe, Func<T, bool> done, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var value = probe();
                if (done(value)) return value;
                await Task.Delay(10);
            }
            throw new TimeoutException($"{what}가 일어나지 않았습니다.");
        }
    }

    private sealed class FakeTranslator : ITranslator
    {
        private int _calls;
        /// <summary>실제로 번역 API를 호출한 횟수 — 양보가 먹었는지 보는 잣대다.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<string?>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<string?>>(texts.Select(t => (string?)$"{targetLang}:{t}").ToList());
        }
    }

    /// <summary>가사를 하나 돌려주는 제공자(양보 경로는 제공자 검색이 성공해야 진입한다).</summary>
    private sealed class StubProvider(string lrc) : ILyricsProvider
    {
        public string ServiceName => "LRCLIB";

        public async IAsyncEnumerable<Lyrics> GetLyricsAsync(
            LyricsSearchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var lyrics = Lyrics.Parse(lrc)!;
            lyrics.Metadata.ServiceName = ServiceName;
            lyrics.Metadata.Request = request;
            yield return lyrics;
        }
    }

    private sealed class EmptyProvider : ILyricsProvider
    {
        public string ServiceName => "Empty";

        public async IAsyncEnumerable<Lyrics> GetLyricsAsync(
            LyricsSearchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FakeSource : INowPlayingSource
    {
        public TrackInfo? CurrentTrack { get; set; }
        public bool IsPlaying { get; set; }
        public event Action<TrackInfo?>? TrackChanged;
        public event Action<bool>? IsPlayingChanged;
        public TimeSpan? GetEstimatedPosition() => TimeSpan.Zero;
        public PlaybackControls GetControls() => PlaybackControls.None;
        public Task<bool> TogglePlayPauseAsync() => Task.FromResult(true);
        public Task<bool> SkipNextAsync() => Task.FromResult(true);
        public Task<bool> SkipPreviousAsync() => Task.FromResult(true);

        public void RaiseTrack(TrackInfo? track) { CurrentTrack = track; TrackChanged?.Invoke(track); }
        public void RaisePlaying(bool playing) { IsPlaying = playing; IsPlayingChanged?.Invoke(playing); }
    }

    private sealed class InlineDispatcher : IEngineDispatcher
    {
        public void Post(Action action) => action();
        public IEngineTimer CreateTimer(TimeSpan interval, Action tick) => new NoopTimer();
        private sealed class NoopTimer : IEngineTimer { public void Start() { } public void Stop() { } }
    }
}
