using System.Threading;
using Windows.Media.Control;

using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace LyricsX.App.Services;

/// <summary>현재 재생 곡 정보</summary>
public sealed record TrackInfo(string Title, string Artist, string Album, TimeSpan? Duration, string SourceAppId)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}

/// <summary>
/// SMTC(GlobalSystemMediaTransportControls) 래퍼.
/// 트랙/재생상태 변경 이벤트와 보간된 재생 위치를 제공한다.
/// 이벤트는 임의 스레드에서 발생하므로 구독자가 UI 마샬링을 책임진다.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private SessionManager? _manager;
    private Session? _session;
    private readonly object _lock = new();
    private Timer? _pollTimer;

    // 위치 떨림 완화용 상태 (같은 곡 재생 중 작은 역행 흡수)
    private TimeSpan _smoothedPosition = TimeSpan.MinValue;
    private string? _smoothedTrackKey;
    private static readonly TimeSpan BackwardTolerance = TimeSpan.FromSeconds(1.0);

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<bool>? IsPlayingChanged;

    public static async Task<NowPlayingService> CreateAsync()
    {
        var service = new NowPlayingService();
        service._manager = await SessionManager.RequestAsync();
        service._manager.CurrentSessionChanged += (_, _) => service.SelectBestSession();
        service._manager.SessionsChanged += (_, _) => service.SelectBestSession();
        service.SelectBestSession();

        // SMTC의 PlaybackInfoChanged 이벤트가 앱(예: Spotify)에 따라 지연되어
        // 정지 후에도 가사가 남는 문제 방지 — 재생 상태를 주기적으로 폴링해 즉시 반영.
        // 아울러 매 폴링마다 최적 세션을 재선택한다: Windows 10에서 Spotify가
        // "현재 세션"으로 잡히지 않아(다른 앱이 current) 인식이 안 되던 문제를 해결.
        service._pollTimer = new Timer(
            _ => { service.SelectBestSession(); service.RefreshPlayback(); }, null,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        return service;
    }

    /// <summary>
    /// 부착할 SMTC 세션을 재선택한다. GetCurrentSession()만 믿지 않고 전체 세션을
    /// 열거해 '재생 중'인 세션을 우선한다(Win10에서 Spotify가 current로 안 잡히는 문제 대응).
    /// 선택이 바뀔 때만 재구독한다.
    /// </summary>
    private void SelectBestSession()
    {
        bool changed;
        lock (_lock)
        {
            var manager = _manager;
            if (manager is null) return;

            var best = PickBestSession(manager);
            if (SameSession(best, _session)) return;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            _session = best;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
            changed = true;
        }

        if (changed)
        {
            _ = RefreshTrackAsync();
            RefreshPlayback();
        }
    }

    /// <summary>current 세션이 재생 중이면 그대로, 아니면 재생 중인 아무 세션, 그래도 없으면 current/첫 세션.</summary>
    private static Session? PickBestSession(SessionManager manager)
    {
        Session? current = null;
        try { current = manager.GetCurrentSession(); } catch { /* 레이스 */ }
        if (current is not null && IsSessionPlaying(current)) return current;

        try
        {
            var sessions = manager.GetSessions();
            Session? first = null;
            foreach (var s in sessions)
            {
                first ??= s;
                if (IsSessionPlaying(s)) return s;
            }
            return current ?? first;
        }
        catch
        {
            return current;
        }
    }

    private static bool IsSessionPlaying(Session session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameSession(Session? a, Session? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.SourceAppUserModelId, b.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshTrackAsync();

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        RefreshPlayback();

    private async Task RefreshTrackAsync()
    {
        Session? session;
        lock (_lock) session = _session;

        TrackInfo? track = null;
        if (session is not null)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : (TimeSpan?)null;
                if (!string.IsNullOrEmpty(props.Title))
                    track = new TrackInfo(props.Title, props.Artist ?? "", props.AlbumTitle ?? "", duration, session.SourceAppUserModelId);
            }
            catch
            {
                // 세션이 사라지는 타이밍 레이스 — 트랙 없음으로 처리
            }
        }

        if (!Equals(track, CurrentTrack))
        {
            CurrentTrack = track;
            TrackChanged?.Invoke(track);
        }
    }

    private void RefreshPlayback()
    {
        Session? session;
        lock (_lock) session = _session;

        var playing = false;
        try
        {
            playing = session?.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            // 세션 소멸 레이스
        }

        if (playing != IsPlaying)
        {
            IsPlaying = playing;
            IsPlayingChanged?.Invoke(playing);
        }
    }

    /// <summary>
    /// 보간된 현재 재생 위치. SMTC 타임라인은 앱별로 갱신이 드물어
    /// LastUpdatedTime 이후 경과분을 더한다 (Spike.Smtc에서 검증).
    /// 새 타임라인 갱신 때 값이 뒤로 튀는 떨림(특히 Apple Music)을 막기 위해
    /// 같은 곡 재생 중 1초 미만의 역행은 흡수한다(큰 변화=시킹은 그대로 반영).
    /// </summary>
    public TimeSpan? GetEstimatedPosition()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return null;

        TimeSpan position;
        bool playing = IsPlaying;
        try
        {
            var timeline = session.GetTimelineProperties();
            position = timeline.Position;
            if (playing)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(30))
                    position += elapsed;
            }
        }
        catch
        {
            return null;
        }

        var trackKey = CurrentTrack is { } t ? $"{t.Title}|{t.Artist}" : null;
        if (playing && trackKey == _smoothedTrackKey && _smoothedPosition != TimeSpan.MinValue)
        {
            var delta = position - _smoothedPosition;
            if (delta < TimeSpan.Zero && delta > -BackwardTolerance)
                position = _smoothedPosition; // 작은 역행은 유지
        }
        _smoothedPosition = position;
        _smoothedTrackKey = trackKey;
        return position;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        lock (_lock)
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session = null;
            }
        }
    }
}
