using LyricsX.Core;
using LyricsX.Core.Search;
using LyricsX.Core.Translation;

namespace LyricsX.Engine;

/// <summary>
/// 현재 표시할 가사 한 줄 (원문 + 번역).
/// Karaoke: 글자 단위 타임태그(있으면 글자 채움), LineSpanSeconds: 라인 표시 구간(초, 라인 단위 폴백용).
/// </summary>
public sealed record DisplayLine(
    string? Content,
    string? Translation,
    InlineTimeTags? Karaoke = null,
    double LineSpanSeconds = 0);

/// <summary>
/// 원본 AppController의 핵심 역할 포팅:
/// 트랙 변경 → 가사 검색 → 재생 위치 틱 → 현재 라인 이벤트.
/// 플랫폼 무관: 재생 소스는 <see cref="INowPlayingSource"/>, 스레드 마샬링·타이머는
/// <see cref="IEngineDispatcher"/>로 추상화되어 Windows/Android/macOS/서버가 공유한다.
/// 이벤트는 IEngineDispatcher가 게시하는 스레드(WPF=UI 스레드)에서 발생한다.
/// </summary>
public sealed class LyricsCoordinator : IDisposable
{
    private readonly INowPlayingSource _nowPlaying;
    private readonly IEngineDispatcher _dispatcher;
    private readonly LyricsSearchService _search;
    private readonly IEngineTimer _timer;

    private CancellationTokenSource? _searchCts;
    private int _lastLineIndex = int.MinValue;

    // 직렬화 표시 상태(StateChanged)용 현재 라인 스냅샷
    private DisplayLine? _currentLine;
    private DateTimeOffset? _currentLineStartedAt;

    public Lyrics? CurrentLyrics { get; private set; }
    public TrackInfo? CurrentTrack => _nowPlaying.CurrentTrack;

    /// <summary>직렬화 가능한 현재 표시 상태(원격 디스플레이·바인딩용).</summary>
    public PlaybackViewState CurrentState { get; private set; } = PlaybackViewState.Empty;

    /// <summary>표시 상태 변경(라인/재생상태). 원격 브로드캐스트·VM 바인딩 대상.</summary>
    public event Action<PlaybackViewState>? StateChanged;

    /// <summary>수동 싱크 오프셋(초). +면 가사가 빨라진다.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>진단 로그 싱크(선택). 소비자가 Log.Write 등을 주입.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>기계번역 서비스 (키 미설정 시 IsEnabled=false로 무동작)</summary>
    public LyricsTranslationService? Translation { get; set; }

    /// <summary>곡 단위 가사 캐시 (히트 시 네트워크 검색 생략)</summary>
    public LyricsCacheStore? Cache { get; set; }

    /// <summary>DeepL target_lang (예: KO). 표시 우선순위 tr:{lang} → tr에도 사용.</summary>
    public string TargetLanguage { get; set; } = "KO";

    /// <summary>대상 언어 번역만 표시(제공자의 다른 언어 번역 숨김). 대상=중국어면 제외(제공자 번역이 곧 중국어).</summary>
    public bool ShowOnlyTargetTranslation { get; set; } = true;

    private string TargetLangLower => TargetLanguage.ToLowerInvariant();

    /// <summary>대상 언어가 중국어(zh/zh-hans/zh-hant)인가 — 제공자 번역(중국어)을 그대로 쓴다.</summary>
    private bool TargetIsChinese => TargetLangLower.StartsWith("zh", StringComparison.Ordinal);

    /// <summary>
    /// 표시할 번역 결정.
    /// - 대상=중국어: 제공자 번역(중국어)을 그대로 우선(없으면 tr:{target}).
    /// - "대상 언어만" 켬: tr:{target}(기계번역)만, 제공자의 다른 언어 번역은 숨김.
    /// - 끔: tr:{target} → 제공자 tr 폴백(기존 동작).
    /// </summary>
    private string? ResolveDisplayTranslation(LineAttachments att)
    {
        if (TargetIsChinese) return att.Translation(null, TargetLangLower);
        if (ShowOnlyTargetTranslation) return att.Translation(TargetLangLower);
        return att.Translation(TargetLangLower, null);
    }

    /// <summary>표시 정책 변경 등으로 현재 라인을 즉시 다시 발행하도록 한다.</summary>
    public void RefreshCurrentLine() => _lastLineIndex = int.MinValue;

    /// <summary>현재 라인 변경 (null = 가사 없음/재생 없음)</summary>
    public event Action<DisplayLine?>? CurrentLineChanged;

    /// <summary>현재 라인 시작 이후 경과 시간(초, 매 틱). 글자/라인 단위 카라오케 채움에 사용.</summary>
    public event Action<double>? LineProgressChanged;

    /// <summary>가사 검색 상태(구조화). 소비자가 현지화한다.</summary>
    public event Action<LyricsStatus>? StatusChanged;

    /// <summary>"틀린 가사"로 표시된 트랙 키 집합(검색·표시 억제). 소비자가 설정과 동기화.</summary>
    public HashSet<string> SuppressedTrackKeys { get; } = new();

    /// <summary>억제 목록 변경 알림(설정 영속화용)</summary>
    public event Action? SuppressedTracksChanged;

    public LyricsCoordinator(INowPlayingSource nowPlaying, IEngineDispatcher dispatcher, LyricsSearchService? search = null)
    {
        _nowPlaying = nowPlaying;
        _dispatcher = dispatcher;
        _search = search ?? new LyricsSearchService();

        _timer = _dispatcher.CreateTimer(TimeSpan.FromMilliseconds(100), Tick);

        _nowPlaying.TrackChanged += track => _dispatcher.Post(() => OnTrackChanged(track));
        _nowPlaying.IsPlayingChanged += playing => _dispatcher.Post(() =>
        {
            if (playing) _timer.Start();
            else _timer.Stop();
            EmitState(); // 재생상태 변화를 표시 상태에 반영
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
        _currentLine = null;
        _currentLineStartedAt = null;
        CurrentLineChanged?.Invoke(null);
        EmitState(); // 새 트랙(제목) 반영, 라인은 아직 없음

        if (track is null)
        {
            StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.NoTrack));
            return;
        }

        // "틀린 가사"로 표시된 곡은 검색·표시하지 않는다
        if (SuppressedTrackKeys.Contains(LyricsCacheStore.MakeKey(track.Title, track.Artist)))
        {
            StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.HiddenByUser, track.ToString()));
            return;
        }

        // 1) 캐시 히트면 네트워크 검색 생략 (번역 포함 저장분이라 오프라인도 동작)
        if (Cache?.Get(track.Title, track.Artist) is { } cached)
        {
            CurrentLyrics = cached;
            _lastLineIndex = int.MinValue;
            StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.Cache, track.ToString(), cached.Metadata.ServiceName ?? ""));
            var cacheCts = new CancellationTokenSource();
            _searchCts = cacheCts;
            await TranslateAsync(cached, cacheCts.Token); // 언어 변경 시 보충 번역
            return;
        }

        StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.Searching, track.ToString()));
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
                    StatusChanged?.Invoke(new LyricsStatus(
                        LyricsStatusKind.Found, track.ToString(), lyrics.Metadata.ServiceName ?? "", lyrics.Quality()));
                    await TranslateAsync(lyrics, cts.Token);
                }
            }

            if (CurrentLyrics is null)
            {
                StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.NotFound, track.ToString()));
            }
            else if (!cts.Token.IsCancellationRequested)
            {
                // 2) 최종 선택본(번역 포함) 캐시 저장
                try
                {
                    Cache?.Set(track.Title, track.Artist, CurrentLyrics);
                    Log?.Invoke($"[cache] 저장: {track}");
                }
                catch (Exception e)
                {
                    Log?.Invoke($"[cache] 저장 실패: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 다음 트랙으로 교체됨
        }
        catch (Exception e)
        {
            Log?.Invoke($"[search] 예외: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 현재 곡을 "틀린 가사"로 표시한다: 캐시에서 제거하고, 표시를 지우고,
    /// 트랙이 유지되는 동안(및 재생 복귀 시) 재검색·표시를 억제한다.
    /// </summary>
    public void MarkWrongLyrics()
    {
        if (CurrentTrack is not { } track) return;

        SuppressedTrackKeys.Add(LyricsCacheStore.MakeKey(track.Title, track.Artist));
        try { Cache?.Remove(track.Title, track.Artist); }
        catch (Exception e) { Log?.Invoke($"[wrong] 캐시 제거 실패: {e.Message}"); }

        _searchCts?.Cancel();
        CurrentLyrics = null;
        _lastLineIndex = int.MinValue;
        _currentLine = null;
        _currentLineStartedAt = null;
        CurrentLineChanged?.Invoke(null);
        EmitState();
        StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.Wrong, track.ToString()));
        SuppressedTracksChanged?.Invoke();
    }

    private void Unsuppress(TrackInfo? track)
    {
        if (track is null) return;
        if (SuppressedTrackKeys.Remove(LyricsCacheStore.MakeKey(track.Title, track.Artist)))
            SuppressedTracksChanged?.Invoke();
    }

    /// <summary>수동 검색 등 외부에서 선택한 가사를 적용하고 캐시를 갱신한다.</summary>
    public async Task UseLyricsAsync(Lyrics lyrics)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        Unsuppress(CurrentTrack); // 사용자가 직접 고른 가사이므로 억제 해제

        CurrentLyrics = lyrics;
        _lastLineIndex = int.MinValue;
        StatusChanged?.Invoke(new LyricsStatus(
            LyricsStatusKind.Manual, CurrentTrack?.ToString() ?? "", lyrics.Metadata.ServiceName ?? ""));
        await TranslateAsync(lyrics, cts.Token);
        if (CurrentTrack is { } track && !cts.Token.IsCancellationRequested)
            Cache?.Set(track.Title, track.Artist, lyrics);
    }

    /// <summary>
    /// 편집된 가사를 캐시에 저장하고(출처=사용자 편집), 여전히 같은 곡이면 즉시 반영한다.
    /// 사용자가 고친 번역을 덮어쓰지 않도록 기계번역(MT)은 실행하지 않는다.
    /// </summary>
    public void SaveEditedLyrics(TrackInfo track, Lyrics lyrics)
    {
        Unsuppress(track); // 편집·저장한 곡은 억제 해제
        lyrics.Metadata.ServiceName = "사용자 편집";
        try
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            Log?.Invoke($"[edit] 저장: {track}");
        }
        catch (Exception e)
        {
            Log?.Invoke($"[edit] 저장 실패: {e.Message}");
        }

        if (CurrentTrack is { } cur && cur.Title == track.Title && cur.Artist == track.Artist)
        {
            _searchCts?.Cancel(); // 진행 중 검색이 편집본을 덮어쓰지 않도록
            CurrentLyrics = lyrics;
            _lastLineIndex = int.MinValue; // 현재 라인 재발행
            StatusChanged?.Invoke(new LyricsStatus(LyricsStatusKind.Edited, track.ToString()));
        }
    }

    /// <summary>대상 언어 MT 보장 후 현재 라인 갱신 (캐시 히트면 즉시, 미스면 API 1회)</summary>
    private async Task TranslateAsync(Lyrics lyrics, CancellationToken ct)
    {
        // 대상=중국어면 제공자 번역(중국어)을 그대로 쓰므로 DeepL을 거치지 않는다.
        if (TargetIsChinese) return;
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

        // 라인 표시 구간: 현재 라인 시작 ~ 다음 라인 시작(없으면 곡 끝/+5초)
        double start = 0, span = 0;
        if (index >= 0)
        {
            start = lyrics.Lines[index].Position;
            var end = next is { } n ? lyrics.Lines[n].Position : lyrics.Length ?? start + 5.0;
            span = end - start;
        }

        if (index != _lastLineIndex)
        {
            _lastLineIndex = index;
            if (index < 0)
            {
                _currentLine = null;
                _currentLineStartedAt = null;
                CurrentLineChanged?.Invoke(null);
            }
            else
            {
                var line = lyrics.Lines[index];
                var display = new DisplayLine(
                    line.Content,
                    ResolveDisplayTranslation(line.Attachments),
                    line.Attachments.GetInlineTimeTags(),
                    span);
                _currentLine = display;
                // 라인이 재생상 시작된 절대 시각(표시측 보간 앵커): 현재 - 라인 내 경과
                _currentLineStartedAt = DateTimeOffset.Now - TimeSpan.FromSeconds(Math.Max(0, adjusted - start));
                CurrentLineChanged?.Invoke(display);
            }
            EmitState();
        }

        if (index >= 0)
            LineProgressChanged?.Invoke(adjusted - start); // 라인 시작 이후 경과(초)
    }

    /// <summary>현재 스냅샷으로 직렬화 표시 상태를 갱신·발행한다.</summary>
    private void EmitState()
    {
        var track = _nowPlaying.CurrentTrack;
        var controls = _nowPlaying.GetControls();
        var line = _currentLine;

        IReadOnlyList<KaraokeMark>? karaoke = null;
        double? karaokeDuration = null;
        if (line?.Karaoke is { } k)
        {
            karaoke = k.Tags.Select(t => new KaraokeMark(t.Index, t.Time)).ToList();
            karaokeDuration = k.Duration;
        }

        CurrentState = new PlaybackViewState(
            _nowPlaying.IsPlaying,
            track?.Title, track?.Artist,
            line?.Content, line?.Translation,
            karaoke, karaokeDuration,
            _currentLineStartedAt,
            line?.LineSpanSeconds ?? 0,
            controls.CanPrevious, controls.CanPlayPause, controls.CanNext);

        StateChanged?.Invoke(CurrentState);
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _timer.Stop();
    }
}
