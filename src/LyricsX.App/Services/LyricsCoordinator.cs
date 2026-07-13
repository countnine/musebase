using System.Windows.Threading;
using LyricsX.Core;
using LyricsX.Core.Search;

namespace LyricsX.App.Services;

/// <summary>현재 표시할 가사 한 줄 (원문 + 번역)</summary>
public sealed record DisplayLine(string? Content, string? Translation);

/// <summary>
/// 원본 AppController의 핵심 역할 포팅:
/// 트랙 변경 → 가사 검색 → 재생 위치 틱 → 현재 라인 이벤트.
/// UI 스레드(Dispatcher)에서 이벤트를 발생시킨다.
/// </summary>
public sealed class LyricsCoordinator : IDisposable
{
    private readonly NowPlayingService _nowPlaying;
    private readonly LyricsSearchService _search;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _searchCts;
    private int _lastLineIndex = int.MinValue;

    public Lyrics? CurrentLyrics { get; private set; }
    public TrackInfo? CurrentTrack => _nowPlaying.CurrentTrack;

    /// <summary>현재 라인 변경 (null = 가사 없음/재생 없음)</summary>
    public event Action<DisplayLine?>? CurrentLineChanged;

    /// <summary>가사 검색 상태 텍스트 (트레이 툴팁 등 상태 표시용)</summary>
    public event Action<string>? StatusChanged;

    public LyricsCoordinator(NowPlayingService nowPlaying, Dispatcher dispatcher, LyricsSearchService? search = null)
    {
        _nowPlaying = nowPlaying;
        _dispatcher = dispatcher;
        _search = search ?? new LyricsSearchService();

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += (_, _) => Tick();

        _nowPlaying.TrackChanged += track => _dispatcher.BeginInvoke(() => OnTrackChanged(track));
        _nowPlaying.IsPlayingChanged += playing => _dispatcher.BeginInvoke(() =>
        {
            if (playing) _timer.Start();
            else _timer.Stop();
        });

        if (_nowPlaying.CurrentTrack is { } current) OnTrackChanged(current);
        if (_nowPlaying.IsPlaying) _timer.Start();
    }

    private async void OnTrackChanged(TrackInfo? track)
    {
        _searchCts?.Cancel();
        CurrentLyrics = null;
        _lastLineIndex = int.MinValue;
        CurrentLineChanged?.Invoke(null);

        if (track is null)
        {
            StatusChanged?.Invoke("재생 중인 곡 없음");
            return;
        }

        StatusChanged?.Invoke($"{track} — 가사 검색 중…");
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            var request = LyricsSearchRequest.ByInfo(
                track.Title, track.Artist, track.Duration?.TotalSeconds ?? 0, limit: 3);

            // 첫 결과 우선 표시 후 더 좋은 후보로 교체 (지연 체감 최소화)
            await foreach (var lyrics in _search.SearchAsync(request, cts.Token))
            {
                if (cts.Token.IsCancellationRequested) return;
                if (CurrentLyrics is null || lyrics.Quality() > CurrentLyrics.Quality())
                {
                    CurrentLyrics = lyrics;
                    _lastLineIndex = int.MinValue; // 라인 재계산 강제
                    StatusChanged?.Invoke($"{track} — {lyrics.Metadata.ServiceName} (q={lyrics.Quality():0.00})");
                }
            }

            if (CurrentLyrics is null)
                StatusChanged?.Invoke($"{track} — 가사를 찾지 못함");
        }
        catch (OperationCanceledException)
        {
            // 다음 트랙으로 교체됨
        }
    }

    private void Tick()
    {
        var lyrics = CurrentLyrics;
        if (lyrics is null) return;

        var position = _nowPlaying.GetEstimatedPosition();
        if (position is null) return;

        var adjusted = position.Value.TotalSeconds + lyrics.TimeDelay;
        var (current, _) = lyrics.LineIndexesAt(adjusted);
        var index = current ?? -1;
        if (index == _lastLineIndex) return;
        _lastLineIndex = index;

        if (index < 0)
        {
            CurrentLineChanged?.Invoke(null);
            return;
        }

        var line = lyrics.Lines[index];
        CurrentLineChanged?.Invoke(new DisplayLine(line.Content, line.Attachments.Translation()));
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _timer.Stop();
    }
}
