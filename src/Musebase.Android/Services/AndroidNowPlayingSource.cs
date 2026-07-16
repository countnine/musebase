using Android.Content;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Musebase.Engine;
// 암시적 using의 Android.Widget.MediaController와 모호 참조(CS0104) 방지
using MediaController = Android.Media.Session.MediaController;

namespace Musebase.Android.Services;

/// <summary>
/// <see cref="INowPlayingSource"/>의 Android 구현 — MediaSessionManager 래퍼.
///
/// 동작 원리:
/// 1) 사용자가 설정에서 알림 접근(notification access)을 켜면
///    <see cref="MediaSessionManager.GetActiveSessions"/>(리스너 ComponentName)로
///    활성 <see cref="MediaController"/> 목록을 얻을 수 있다.
/// 2) 재생 중인 컨트롤러를 우선 선택하고, 콜백(<see cref="MediaController.Callback"/>)과
///    주기 폴링(500ms)으로 메타데이터/재생 상태 변화를 감지한다.
///    (폴링은 Windows판 NowPlayingService와 같은 이유 — 앱에 따라 콜백이 지연/누락된다.)
/// 3) 위치는 PlaybackState.Position + (elapsedRealtime - LastPositionUpdateTime) × 속도로
///    보간하고, 같은 곡 재생 중 1초 미만의 역행은 흡수한다(타임라인 갱신 떨림 완화).
///
/// 모든 상태 갱신·이벤트 발화는 메인 루퍼에서 일어난다(생성/Start를 메인 스레드에서 호출할 것).
/// 계약상 이벤트 스레드 마샬링은 구독자 책임이지만, 이 구현은 메인 스레드로 정렬해 준다.
/// </summary>
public sealed class AndroidNowPlayingSource : Java.Lang.Object,
    INowPlayingSource, MediaSessionManager.IOnActiveSessionsChangedListener
{
    private static readonly TimeSpan BackwardTolerance = TimeSpan.FromSeconds(1.0);
    private const int PollIntervalMs = 500;

    private readonly Context _context;
    private readonly ComponentName _listenerComponent;
    private readonly Handler _handler = new(Looper.MainLooper!);

    private MediaSessionManager? _manager;
    private MediaController? _controller;
    private ControllerCallback? _callback;
    private bool _started;
    private bool _sessionListenerRegistered;

    // 위치 떨림 완화 상태 (같은 곡 재생 중 작은 역행 흡수)
    private TimeSpan _smoothedPosition = TimeSpan.MinValue;
    private string? _smoothedTrackKey;

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<bool>? IsPlayingChanged;

    public AndroidNowPlayingSource(Context context)
    {
        _context = context.ApplicationContext ?? context;
        _listenerComponent = new ComponentName(
            _context, Java.Lang.Class.FromType(typeof(MediaListenerService)));
    }

    /// <summary>알림 접근 권한이 이 앱에 허용되어 있는지.</summary>
    public bool HasNotificationAccess
    {
        get
        {
            var enabled = global::Android.Provider.Settings.Secure.GetString(
                _context.ContentResolver, "enabled_notification_listeners");
            return enabled?.Contains(_context.PackageName ?? "", StringComparison.Ordinal) == true;
        }
    }

    /// <summary>
    /// 감지 시작(메인 스레드에서 호출). 권한이 아직 없으면 폴링만 돌며
    /// 권한이 생기는 즉시 세션 구독을 시작한다 — 재호출해도 안전(멱등).
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _manager ??= (MediaSessionManager?)_context.GetSystemService(Context.MediaSessionService);
        PollOnce();
        SchedulePoll();
    }

    public void Stop()
    {
        _started = false;
        _handler.RemoveCallbacksAndMessages(null);
        if (_sessionListenerRegistered && _manager is not null)
        {
            try { _manager.RemoveOnActiveSessionsChangedListener(this); } catch { /* 이미 해제 */ }
            _sessionListenerRegistered = false;
        }
        AttachController(null);
    }

    // ---- MediaSessionManager.IOnActiveSessionsChangedListener ----

    public void OnActiveSessionsChanged(IList<MediaController>? controllers) =>
        SelectBestController(controllers);

    // ---- 세션 선택 ----

    private void SchedulePoll()
    {
        if (!_started) return;
        _handler.PostDelayed(() => { PollOnce(); SchedulePoll(); }, PollIntervalMs);
    }

    /// <summary>폴링 1회: 권한 확인 → 리스너 등록 → 최적 세션 재선택 → 상태 갱신.</summary>
    private void PollOnce()
    {
        if (_manager is null || !HasNotificationAccess)
        {
            // 권한이 회수됐거나 아직 없음 — 세션 없음으로 정리하고 다음 폴링에서 재시도.
            _sessionListenerRegistered = false;
            AttachController(null);
            return;
        }

        try
        {
            if (!_sessionListenerRegistered)
            {
                _manager.AddOnActiveSessionsChangedListener(this, _listenerComponent);
                _sessionListenerRegistered = true;
            }
            SelectBestController(_manager.GetActiveSessions(_listenerComponent));
        }
        catch (Java.Lang.SecurityException)
        {
            // 권한 회수 레이스 — 다음 폴링에서 HasNotificationAccess가 걸러 준다.
            _sessionListenerRegistered = false;
            AttachController(null);
            return;
        }

        RefreshTrack();
        RefreshPlayback();
    }

    /// <summary>
    /// 부착할 컨트롤러를 결정한다: 재생 중인 세션 우선, 없으면 목록 첫 세션
    /// (GetActiveSessions는 최근 활성 순으로 정렬돼 있다). 바뀔 때만 재구독.
    /// </summary>
    private void SelectBestController(IList<MediaController>? controllers)
    {
        MediaController? best = null;
        if (controllers is not null)
        {
            foreach (var c in controllers)
            {
                best ??= c;
                if (c.PlaybackState?.State == PlaybackStateCode.Playing) { best = c; break; }
            }
        }
        AttachController(best);
    }

    private void AttachController(MediaController? controller)
    {
        if (SameController(controller, _controller)) return;

        if (_controller is not null && _callback is not null)
        {
            try { _controller.UnregisterCallback(_callback); } catch { /* 세션 소멸 레이스 */ }
        }

        _controller = controller;
        if (_controller is not null)
        {
            _callback ??= new ControllerCallback(this);
            _controller.RegisterCallback(_callback, _handler);
        }

        global::Android.Util.Log.Info("Musebase",
            $"media session attached: {controller?.PackageName ?? "(none)"}");
        RefreshTrack();
        RefreshPlayback();
    }

    private static bool SameController(MediaController? a, MediaController? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        // SessionToken 동일성이 정확하지만, 스파이크에선 패키지 단위 비교로 충분하다.
        return string.Equals(a.PackageName, b.PackageName, StringComparison.Ordinal);
    }

    // ---- 상태 갱신 ----

    private void RefreshTrack()
    {
        TrackInfo? track = null;
        var controller = _controller;
        if (controller is not null)
        {
            try
            {
                var md = controller.Metadata;
                var title = md?.GetString(MediaMetadata.MetadataKeyTitle);
                if (!string.IsNullOrEmpty(title))
                {
                    var durationMs = md!.GetLong(MediaMetadata.MetadataKeyDuration);
                    track = new TrackInfo(
                        title,
                        md.GetString(MediaMetadata.MetadataKeyArtist) ?? "",
                        md.GetString(MediaMetadata.MetadataKeyAlbum) ?? "",
                        durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : null,
                        controller.PackageName ?? "");
                }
            }
            catch { /* 세션 소멸 레이스 — 트랙 없음 처리 */ }
        }

        if (!Equals(track, CurrentTrack))
        {
            CurrentTrack = track;
            TrackChanged?.Invoke(track);
        }
    }

    private void RefreshPlayback()
    {
        var playing = false;
        try { playing = _controller?.PlaybackState?.State == PlaybackStateCode.Playing; }
        catch { /* 세션 소멸 레이스 */ }

        if (playing != IsPlaying)
        {
            IsPlaying = playing;
            IsPlayingChanged?.Invoke(playing);
        }
    }

    /// <summary>
    /// 보간된 현재 재생 위치. PlaybackState는 갱신이 드물어
    /// LastPositionUpdateTime 이후 경과분 × 재생 속도를 더한다.
    /// 같은 곡 재생 중 1초 미만의 역행은 흡수한다(시킹 등 큰 변화는 그대로 반영).
    /// </summary>
    public TimeSpan? GetEstimatedPosition()
    {
        PlaybackState? state;
        try { state = _controller?.PlaybackState; }
        catch { return null; }
        if (state is null) return null;

        var positionMs = (double)state.Position;
        var playing = state.State == PlaybackStateCode.Playing;
        if (playing)
        {
            var elapsedMs = SystemClock.ElapsedRealtime() - state.LastPositionUpdateTime;
            if (elapsedMs > 0 && elapsedMs < 30 * 60 * 1000)
            {
                var speed = state.PlaybackSpeed;
                positionMs += elapsedMs * (speed > 0 ? speed : 1f);
            }
        }
        var position = TimeSpan.FromMilliseconds(positionMs);

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

    /// <summary>현재 세션의 컨트롤 가용 여부(PlaybackState.Actions 비트).</summary>
    public PlaybackControls GetControls()
    {
        long actions;
        try { actions = _controller?.PlaybackState?.Actions ?? 0; }
        catch { return PlaybackControls.None; }

        return new PlaybackControls(
            (actions & PlaybackState.ActionSkipToPrevious) != 0,
            (actions & (PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause)) != 0,
            (actions & PlaybackState.ActionSkipToNext) != 0);
    }

    public Task<bool> TogglePlayPauseAsync()
    {
        var controller = _controller;
        var tc = controller?.GetTransportControls();
        if (tc is null) return Task.FromResult(false);
        try
        {
            if (controller!.PlaybackState?.State == PlaybackStateCode.Playing) tc.Pause();
            else tc.Play();
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }

    public Task<bool> SkipNextAsync()
    {
        var tc = _controller?.GetTransportControls();
        if (tc is null) return Task.FromResult(false);
        try { tc.SkipToNext(); return Task.FromResult(true); }
        catch { return Task.FromResult(false); }
    }

    public Task<bool> SkipPreviousAsync()
    {
        var tc = _controller?.GetTransportControls();
        if (tc is null) return Task.FromResult(false);
        try { tc.SkipToPrevious(); return Task.FromResult(true); }
        catch { return Task.FromResult(false); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Stop();
        base.Dispose(disposing);
    }

    /// <summary>선택된 컨트롤러의 변경 콜백 → 소스 상태 갱신으로 전달.</summary>
    private sealed class ControllerCallback : MediaController.Callback
    {
        private readonly AndroidNowPlayingSource _owner;
        public ControllerCallback(AndroidNowPlayingSource owner) => _owner = owner;

        public override void OnMetadataChanged(MediaMetadata? metadata) => _owner.RefreshTrack();
        public override void OnPlaybackStateChanged(PlaybackState? state) => _owner.RefreshPlayback();
        public override void OnSessionDestroyed() => _owner.AttachController(null);
    }
}
