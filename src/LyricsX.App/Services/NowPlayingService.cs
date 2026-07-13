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

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<bool>? IsPlayingChanged;

    public static async Task<NowPlayingService> CreateAsync()
    {
        var service = new NowPlayingService();
        service._manager = await SessionManager.RequestAsync();
        service._manager.CurrentSessionChanged += (_, _) => service.AttachCurrentSession();
        service.AttachCurrentSession();
        return service;
    }

    private void AttachCurrentSession()
    {
        lock (_lock)
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            _session = _manager?.GetCurrentSession();
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
        }
        _ = RefreshTrackAsync();
        RefreshPlayback();
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
    /// </summary>
    public TimeSpan? GetEstimatedPosition()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return null;

        try
        {
            var timeline = session.GetTimelineProperties();
            var position = timeline.Position;
            if (IsPlaying)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(30))
                    position += elapsed;
            }
            return position;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
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
