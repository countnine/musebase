using System.Windows.Threading;
using LyricsX.Core;
using LyricsX.Core.Search;
using LyricsX.Core.Translation;

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

    /// <summary>수동 싱크 오프셋(초). +면 가사가 빨라진다.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>기계번역 서비스 (키 미설정 시 IsEnabled=false로 무동작)</summary>
    public LyricsTranslationService? Translation { get; set; }

    /// <summary>곡 단위 가사 캐시 (히트 시 네트워크 검색 생략)</summary>
    public LyricsCacheStore? Cache { get; set; }

    /// <summary>DeepL target_lang (예: KO). 표시 우선순위 tr:{lang} → tr에도 사용.</summary>
    public string TargetLanguage { get; set; } = "KO";

    private string TargetLangLower => TargetLanguage.ToLowerInvariant();

    /// <summary>현재 라인 변경 (null = 가사 없음/재생 없음)</summary>
    public event Action<DisplayLine?>? CurrentLineChanged;

    /// <summary>현재 라인 진행 비율 0~1 (카라오케 채움용, 매 틱)</summary>
    public event Action<double>? LineProgressChanged;

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

    }

    /// <summary>
    /// 이벤트 구독·속성(Cache/Translation) 배선이 끝난 뒤 호출.
    /// 생성자에서 시작하면 이니셜라이저 속성이 아직 null이라 캐시/번역이 무시된다.
    /// </summary>
    public void Start()
    {
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

        // 1) 캐시 히트면 네트워크 검색 생략 (번역 포함 저장분이라 오프라인도 동작)
        if (Cache?.Get(track.Title, track.Artist) is { } cached)
        {
            CurrentLyrics = cached;
            _lastLineIndex = int.MinValue;
            StatusChanged?.Invoke($"{track} — 캐시 ({cached.Metadata.ServiceName})");
            var cacheCts = new CancellationTokenSource();
            _searchCts = cacheCts;
            await TranslateAsync(cached, cacheCts.Token); // 언어 변경 시 보충 번역
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
                    await TranslateAsync(lyrics, cts.Token);
                }
            }

            if (CurrentLyrics is null)
            {
                StatusChanged?.Invoke($"{track} — 가사를 찾지 못함");
            }
            else if (!cts.Token.IsCancellationRequested)
            {
                // 2) 최종 선택본(번역 포함) 캐시 저장
                try
                {
                    Cache?.Set(track.Title, track.Artist, CurrentLyrics);
                    Log.Write($"[cache] 저장: {track}");
                }
                catch (Exception e)
                {
                    Log.Write($"[cache] 저장 실패: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 다음 트랙으로 교체됨
        }
        catch (Exception e)
        {
            Log.Write($"[search] 예외: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>수동 검색 등 외부에서 선택한 가사를 적용하고 캐시를 갱신한다.</summary>
    public async Task UseLyricsAsync(Lyrics lyrics)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        CurrentLyrics = lyrics;
        _lastLineIndex = int.MinValue;
        StatusChanged?.Invoke($"{CurrentTrack} — 수동 선택 ({lyrics.Metadata.ServiceName})");
        await TranslateAsync(lyrics, cts.Token);
        if (CurrentTrack is { } track && !cts.Token.IsCancellationRequested)
            Cache?.Set(track.Title, track.Artist, lyrics);
    }

    /// <summary>대상 언어 MT 보장 후 현재 라인 갱신 (캐시 히트면 즉시, 미스면 API 1회)</summary>
    private async Task TranslateAsync(Lyrics lyrics, CancellationToken ct)
    {
        if (Translation is not { IsEnabled: true } service) return;
        try
        {
            var changed = await service.EnsureTranslatedAsync(lyrics, TargetLanguage, ct);
            if (changed > 0 && ReferenceEquals(CurrentLyrics, lyrics))
                _lastLineIndex = int.MinValue; // 번역 반영 위해 현재 라인 재발행
        }
        catch (OperationCanceledException)
        {
            // 트랙 교체됨
        }
    }

    private void Tick()
    {
        var lyrics = CurrentLyrics;
        if (lyrics is null) return;

        var position = _nowPlaying.GetEstimatedPosition();
        if (position is null) return;

        var adjusted = position.Value.TotalSeconds + lyrics.TimeDelay + ManualOffsetSeconds;
        var (current, next) = lyrics.LineIndexesAt(adjusted);
        var index = current ?? -1;

        if (index != _lastLineIndex)
        {
            _lastLineIndex = index;
            if (index < 0)
            {
                CurrentLineChanged?.Invoke(null);
            }
            else
            {
                var line = lyrics.Lines[index];
                // 표시 우선순위: tr:{target}(MT) → tr(제공자) 폴백
                CurrentLineChanged?.Invoke(new DisplayLine(
                    line.Content, line.Attachments.Translation(TargetLangLower, null)));
            }
        }

        // 라인 진행 비율: 다음 라인 시작(없으면 곡 끝/+5초)까지를 100%로
        if (index >= 0)
        {
            var start = lyrics.Lines[index].Position;
            var end = next is { } n ? lyrics.Lines[n].Position
                : lyrics.Length ?? start + 5.0;
            var span = end - start;
            LineProgressChanged?.Invoke(span > 0 ? Math.Clamp((adjusted - start) / span, 0.0, 1.0) : 0.0);
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _timer.Stop();
    }
}
