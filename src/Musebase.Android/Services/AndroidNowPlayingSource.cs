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
    // 광고 원시 신호 로그를 곡당 한 줄로 줄이기 위한 직전 값.
    private string? _lastAdSignature;

    // 재생 소스 선택(Windows NowPlayingService와 같은 규칙): "auto" = 자동 감지,
    // 그 외 = 특정 앱 패키지로 고정.
    private string _sourceMode = AutoSource;
    // 자동 모드에서 영상·브라우저 앱(YouTube·크롬 등)을 음악 소스로 포함할지. 기본 제외 —
    // 영상 재생 중 엉뚱한 곡의 가사를 찾아 표시하는 것을 막는다.
    private bool _includeVideoApps;
    // 자동 모드에서 "이 앱들만" 음악 소스로 인정하는 화이트리스트(비면 자동 = 종전 규칙).
    private readonly HashSet<string> _preferredSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>자동 감지 식별자.</summary>
    public const string AutoSource = "auto";

    /// <summary>영상·브라우저로 간주할 패키지 토큰(부분 일치, 소문자).</summary>
    private static readonly string[] VideoAppTokens =
    {
        "com.google.android.youtube", "com.google.android.videos",
        "com.android.chrome", "org.mozilla", "com.microsoft.emmx", "com.opera",
        "com.brave", "com.sec.android.app.sbrowser", "com.duckduckgo", "com.kiwibrowser",
        "com.netflix", "tv.twitch", "com.instagram", "com.zhiliaoapp.musically", "com.facebook",
    };

    /// <summary>자동 소스 감지 모드 여부.</summary>
    private bool IsAuto => string.Equals(_sourceMode, AutoSource, StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoApp(string? package)
    {
        if (string.IsNullOrEmpty(package)) return false;
        // YouTube Music은 음악 앱이므로 제외 대상이 아니다(패키지에 youtube가 들어가 먼저 걸러 준다).
        if (package!.StartsWith("com.google.android.apps.youtube.music", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var token in VideoAppTokens)
            if (package.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>현재 활성 세션의 앱 패키지 목록(설정 화면의 소스 선택 목록용). 최근 활성 순.</summary>
    public IReadOnlyList<string> ActiveSessionPackages { get; private set; } = Array.Empty<string>();

    /// <summary>선택된 소스 모드("auto" 또는 패키지명).</summary>
    public string SourceMode => _sourceMode;

    /// <summary>
    /// 재생 소스를 적용한다(설정 저장 시 호출). Windows <c>NowPlayingService.SetSource</c>와 대칭.
    /// 즉시 재선택하므로 다음 폴링을 기다리지 않는다.
    /// </summary>
    public void SetSource(string? mode, bool includeVideoApps, IEnumerable<string>? preferredSources = null)
    {
        _sourceMode = string.IsNullOrWhiteSpace(mode) ? AutoSource : mode!.Trim();
        _includeVideoApps = includeVideoApps;
        _preferredSources.Clear();
        if (preferredSources is not null)
            foreach (var p in preferredSources)
                if (!string.IsNullOrWhiteSpace(p)) _preferredSources.Add(p.Trim());

        global::Android.Util.Log.Info("Musebase",
            $"source: 모드={_sourceMode}, 영상앱포함={_includeVideoApps}, " +
            $"선호앱={(_preferredSources.Count == 0 ? "(자동)" : string.Join(",", _preferredSources))}");
        if (_started) PollOnce();
    }

    // 위치 떨림 완화 상태 (같은 곡 재생 중 작은 역행 흡수)
    private TimeSpan _smoothedPosition = TimeSpan.MinValue;
    private string? _smoothedTrackKey;

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }

    /// <summary>
    /// 현재 세션이 광고를 재생 중인지(<see cref="AdSignals"/> 판정). 광고 뮤트 기능이 쓴다.
    ///
    /// <b><see cref="CurrentTrack"/>과 독립적으로 계산한다</b> — 광고 구간에는 제목이 비어
    /// <see cref="RefreshTrack"/>이 트랙을 만들지 않는 경우가 있는데, 그때도 광고임은 알아야 한다.
    /// </summary>
    public bool IsAdvertisement { get; private set; }

    /// <summary>
    /// 현재 광고를 구분하는 값(<c>mediaId</c>, 예: <c>spotify:ad:d892a38…</c>). 광고가 아니면 null.
    /// 광고가 2개 연속일 때 "다음 광고로 넘어갔다"를 알아내는 유일한 단서다 —
    /// 그 사이 재생 공백에서는 판정을 보류하므로 상태 전이만으로는 구분되지 않는다.
    /// </summary>
    public string? AdvertisementId { get; private set; }

    /// <summary>부착된 세션의 앱 패키지(광고 판정을 Spotify로 한정할 때 쓴다). 없으면 null.</summary>
    public string? CurrentSourcePackage { get; private set; }

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<bool>? IsPlayingChanged;

    /// <summary>
    /// 광고 여부 <b>또는 광고 식별자</b>가 바뀌면 발화한다(인자는 현재 광고 여부).
    ///
    /// 식별자까지 보는 이유: 광고가 2개 연속일 때 <see cref="IsAdvertisement"/>는 계속 true라
    /// 여부만 보면 세그먼트 전환(1/2 → 2/2)을 놓친다. 그러면 구독자가 다음 폴링까지
    /// 최대 1초를 기다리게 된다.
    /// </summary>
    public event Action<bool>? AdvertisementChanged;

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
        RefreshAdvertisement();
        RefreshPlayback();
    }

    /// <summary>
    /// 부착할 컨트롤러를 결정한다: 후보(소스 모드·영상 앱 필터 통과) 중 재생 중인 세션 우선,
    /// 없으면 후보 첫 세션(GetActiveSessions는 최근 활성 순). 바뀔 때만 재구독.
    /// 필터에서 제외된 세션도 <see cref="ActiveSessionPackages"/>에는 남긴다(설정 화면 목록용).
    /// </summary>
    private void SelectBestController(IList<MediaController>? controllers)
    {
        var packages = new List<string>();
        MediaController? best = null;
        if (controllers is not null)
        {
            foreach (var c in controllers)
            {
                var package = c.PackageName;
                if (!string.IsNullOrEmpty(package) && !packages.Contains(package!)) packages.Add(package!);

                if (!IsCandidate(package)) continue;
                best ??= c;
                if (c.PlaybackState?.State == PlaybackStateCode.Playing) { best = c; break; }
            }
        }
        ActiveSessionPackages = packages;
        AttachController(best);
    }

    /// <summary>
    /// 이 세션을 가사 소스로 쓸지 판정한다.
    /// 고정 모드면 패키지 일치. 자동 모드에서는 <see cref="_preferredSources"/>가 지정돼 있으면
    /// **그 앱들만** 후보로 본다(팟캐스트·영상 앱이 끼어드는 것을 원천 차단).
    /// 비어 있으면(기본) 종전대로 영상 앱 제외 규칙만 적용한다.
    /// </summary>
    private bool IsCandidate(string? package)
    {
        if (!IsAuto) return string.Equals(package, _sourceMode, StringComparison.OrdinalIgnoreCase);
        if (_preferredSources.Count > 0)
            return package is not null && _preferredSources.Contains(package);
        return _includeVideoApps || !IsVideoApp(package);
    }

    private void AttachController(MediaController? controller)
    {
        if (SameController(controller, _controller)) return;

        if (_controller is not null && _callback is not null)
        {
            try { _controller.UnregisterCallback(_callback); } catch { /* 세션 소멸 레이스 */ }
        }

        _controller = controller;
        CurrentSourcePackage = controller?.PackageName;
        if (_controller is not null)
        {
            _callback ??= new ControllerCallback(this);
            _controller.RegisterCallback(_callback, _handler);
        }

        global::Android.Util.Log.Info("Musebase",
            $"media session attached: {controller?.PackageName ?? "(none)"}");
        RefreshTrack();
        RefreshAdvertisement();
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
                    var artist = md!.GetString(MediaMetadata.MetadataKeyArtist) ?? "";
                    var album = md.GetString(MediaMetadata.MetadataKeyAlbum) ?? "";

                    // 광고 구간은 곡이 아니다 — 가사를 찾지도, 가사 서버에 올리지도 않게 트랙을
                    // 만들지 않는다(실제로 "Spotify / 광고 • 1/2"가 서버에 저장돼 있었다).
                    // IsAdvertisement 속성에 기대지 않고 같은 메타데이터에서 직접 판정한다 —
                    // RefreshAdvertisement가 이 뒤에 돌아 그 값은 아직 이전 곡 기준이다.
                    var isAd = AdSignals.LooksLikeAd(
                        md.GetLong(AdSignals.AdvertisementMetadataKey),
                        md.GetString(MediaMetadata.MetadataKeyMediaId),
                        artist, album);

                    if (!isAd)
                    {
                        var durationMs = md.GetLong(MediaMetadata.MetadataKeyDuration);
                        track = new TrackInfo(
                            title, artist, album,
                            durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : null,
                            controller.PackageName ?? "");
                    }
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

    /// <summary>
    /// 광고 여부를 세션 메타데이터에서 직접 읽는다(<see cref="RefreshTrack"/>과 독립 — 위 속성 주석 참고).
    /// 판정 규칙 자체는 Android 무의존인 <see cref="AdSignals"/>가 갖는다.
    /// </summary>
    private void RefreshAdvertisement()
    {
        var isAd = false;
        string? adId = null;
        var controller = _controller;

        if (controller is not null)
        {
            try
            {
                var md = controller.Metadata;
                if (md is not null)
                {
                    var flag = md.GetLong(AdSignals.AdvertisementMetadataKey);
                    var mediaId = md.GetString(MediaMetadata.MetadataKeyMediaId);
                    var artist = md.GetString(MediaMetadata.MetadataKeyArtist);
                    var album = md.GetString(MediaMetadata.MetadataKeyAlbum);

                    isAd = AdSignals.LooksLikeAd(flag, mediaId, artist, album);
                    if (isAd) adId = mediaId;
                    LogRawSignals(controller.PackageName, flag, mediaId, artist, album, isAd);
                }
            }
            catch { /* 세션 소멸 레이스 — 광고 아님으로 처리 */ }
        }

        var previousIsAd = IsAdvertisement;
        var previousId = AdvertisementId;

        IsAdvertisement = isAd;
        AdvertisementId = isAd ? adId : null;

        // 광고가 계속되는 동안에도 식별자는 바뀐다(1/2 → 2/2). 그 전환도 알려야 한다.
        if (isAd == previousIsAd &&
            string.Equals(AdvertisementId, previousId, StringComparison.Ordinal))
            return;

        AdvertisementChanged?.Invoke(isAd);
    }

    /// <summary>
    /// 광고 판정에 쓰인 원시 값을 곡이 바뀔 때마다 한 줄 남긴다(Spotify 세션만).
    ///
    /// 이게 이 기능의 프로브다 — <c>dumpsys media_session</c>은 metadata를 제목/아티스트/앨범
    /// 3개로만 덤프해서 <c>ADVERTISEMENT</c> 플래그가 보이지 않는다. 광고가 안 잡힐 때
    /// <c>adb logcat -s Musebase</c>로 Spotify가 실제로 무엇을 내보내는지 확인하는 유일한 경로다.
    /// 값이 바뀔 때만 찍으므로 곡당 한 줄이다.
    /// </summary>
    private void LogRawSignals(
        string? package, long flag, string? mediaId, string? artist, string? album, bool isAd)
    {
        if (!string.Equals(package, AdMuteController.SpotifyPackage, StringComparison.OrdinalIgnoreCase))
            return;

        var signature = $"{flag}|{mediaId}|{artist}|{album}";
        if (signature == _lastAdSignature) return;
        _lastAdSignature = signature;

        global::Android.Util.Log.Info("Musebase",
            $"ad-signals: flag={flag} mediaId='{mediaId}' artist='{artist}' album='{album}' → ad={isAd}");
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

        public override void OnMetadataChanged(MediaMetadata? metadata)
        {
            _owner.RefreshTrack();
            _owner.RefreshAdvertisement();
        }
        public override void OnPlaybackStateChanged(PlaybackState? state) => _owner.RefreshPlayback();
        public override void OnSessionDestroyed() => _owner.AttachController(null);
    }
}
