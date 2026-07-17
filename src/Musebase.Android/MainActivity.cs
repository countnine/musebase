using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Musebase.Engine;

namespace Musebase.Android;

/// <summary>
/// Phase 2 UI — 앱 내 동기 가사 표시(오버레이는 다음 단계). 하는 일:
/// 1) 알림 접근 권한 상태 표시 + 시스템 설정으로 이동하는 버튼
/// 2) 가사 영역: 현재 줄(크게) + 번역(아래) + 검색 상태 문구
///    — <see cref="MusebaseApp"/>이 조립한 <see cref="LyricsCoordinator"/>의
///    StateChanged/StatusChanged 구독으로 갱신(이벤트는 메인 스레드로 정렬돼 있다)
/// 3) 감지된 곡명/아티스트/위치/소스앱을 1초마다 갱신 표시(스파이크 유지)
/// 엔진·소스는 Application 소유이므로 화면 회전에도 유지되고, 이 Activity는 구독만 붙였다 뗀다.
/// 레이아웃 리소스 없이 코드로 UI를 만들어 표면적을 최소화한다.
/// </summary>
[Activity(
    Label = "Musebase",
    Name = "com.countnine.musebase.MainActivity",
    MainLauncher = true,
    Exported = true)]
public sealed class MainActivity : Activity
{
    private const int UiRefreshMs = 1000;

    private readonly Handler _handler = new(Looper.MainLooper!);
    private TextView? _permissionText;
    private TextView? _overlayPermissionText;
    private Button? _overlayToggleButton;
    private TextView? _lyricsStatusText;
    private TextView? _lineText;
    private TextView? _translationText;
    private TextView? _statusText;
    private bool _uiLoopRunning;

    // 구독 해제를 위해 델리게이트 보관
    private Action<PlaybackViewState>? _onStateChanged;
    private Action<LyricsStatus>? _onStatusChanged;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // ---- UI (코드 생성) ----
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetPadding(48, 96, 48, 48);

        var title = new TextView(this) { Text = "Musebase" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
        root.AddView(title);

        _permissionText = new TextView(this);
        _permissionText.SetPadding(0, 32, 0, 0);
        root.AddView(_permissionText);

        var permissionButton = new Button(this) { Text = "알림 접근 권한 설정 열기" };
        permissionButton.Click += (_, _) =>
            StartActivity(new Intent(global::Android.Provider.Settings.ActionNotificationListenerSettings));
        root.AddView(permissionButton);

        // ---- 오버레이(다른 앱 위 표시) ----
        _overlayPermissionText = new TextView(this);
        _overlayPermissionText.SetPadding(0, 32, 0, 0);
        root.AddView(_overlayPermissionText);

        var overlayPermissionButton = new Button(this) { Text = "오버레이 권한 허용" };
        overlayPermissionButton.Click += (_, _) => RequestOverlayPermission();
        root.AddView(overlayPermissionButton);

        _overlayToggleButton = new Button(this) { Text = "가사 오버레이 켜기" };
        _overlayToggleButton.Click += (_, _) => ToggleOverlay();
        root.AddView(_overlayToggleButton);

        // ---- 가사 영역 ----
        _lyricsStatusText = new TextView(this) { Text = "가사 대기 중" };
        _lyricsStatusText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13f);
        _lyricsStatusText.SetPadding(0, 48, 0, 0);
        root.AddView(_lyricsStatusText);

        _lineText = new TextView(this) { Text = "♪" };
        _lineText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 26f);
        _lineText.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        _lineText.SetPadding(0, 16, 0, 0);
        root.AddView(_lineText);

        _translationText = new TextView(this) { Visibility = ViewStates.Gone };
        _translationText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 17f);
        _translationText.SetPadding(0, 8, 0, 0);
        root.AddView(_translationText);

        // ---- 재생 감지 정보(스파이크 유지) ----
        _statusText = new TextView(this) { Text = "감지 대기 중…" };
        _statusText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        _statusText.SetPadding(0, 64, 0, 0);
        root.AddView(_statusText);

        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.AddView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(scroll, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // ---- 엔진 구독 (조립은 MusebaseApp이 이미 완료) ----
        if (MusebaseApp.Instance is { } app)
        {
            _onStateChanged = RenderLine;
            _onStatusChanged = s => RenderLyricsStatus(s);
            app.Coordinator.StateChanged += _onStateChanged;
            app.Coordinator.StatusChanged += _onStatusChanged;
            RenderLine(app.Coordinator.CurrentState); // 초기 스냅샷 반영
            RenderLyricsStatus(app.LastStatus);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!_uiLoopRunning) { _uiLoopRunning = true; UiTick(); }
    }

    protected override void OnPause()
    {
        base.OnPause();
        _uiLoopRunning = false;
        _handler.RemoveCallbacksAndMessages(null);
    }

    /// <summary>1초마다 권한/감지 상태 텍스트 갱신.</summary>
    private void UiTick()
    {
        if (!_uiLoopRunning) return;
        RenderStatus();
        _handler.PostDelayed(UiTick, UiRefreshMs);
    }

    /// <summary>현재 가사 줄 + 번역 갱신(StateChanged — 라인/재생상태 변화 시 발화).</summary>
    private void RenderLine(PlaybackViewState state)
    {
        if (_lineText is null || _translationText is null) return;

        _lineText.Text = string.IsNullOrEmpty(state.LineContent) ? "♪" : state.LineContent;
        if (string.IsNullOrEmpty(state.LineTranslation))
        {
            _translationText.Visibility = ViewStates.Gone;
        }
        else
        {
            _translationText.Text = state.LineTranslation;
            _translationText.Visibility = ViewStates.Visible;
        }
    }

    /// <summary>가사 검색 상태 문구(엔진의 구조화 상태 → 간단 한국어. i18n은 다음 단계).</summary>
    private void RenderLyricsStatus(LyricsStatus s)
    {
        if (_lyricsStatusText is null) return;
        _lyricsStatusText.Text = s.Kind switch
        {
            LyricsStatusKind.NoTrack => "재생 중인 곡 없음",
            LyricsStatusKind.HiddenByUser => "이 곡은 틀린 가사로 표시되어 숨김",
            LyricsStatusKind.Cache => $"가사: 캐시 · {s.Service}",
            LyricsStatusKind.Searching => "가사 검색 중…",
            LyricsStatusKind.Found => $"가사: {s.Service} (품질 {s.Quality ?? 0:0.00})",
            LyricsStatusKind.NotFound => "가사를 찾지 못했습니다",
            LyricsStatusKind.Wrong => "틀린 가사로 표시됨",
            LyricsStatusKind.Manual => $"가사: 수동 선택 · {s.Service}",
            LyricsStatusKind.Edited => "가사: 사용자 편집",
            _ => "",
        };
    }

    /// <summary>오버레이 그리기 권한 요청(시스템 설정의 "다른 앱 위에 표시" 화면으로 이동).</summary>
    private void RequestOverlayPermission()
    {
        if (global::Android.Provider.Settings.CanDrawOverlays(this)) return;
        StartActivity(new Intent(
            global::Android.Provider.Settings.ActionManageOverlayPermission,
            global::Android.Net.Uri.Parse("package:" + PackageName)));
    }

    /// <summary>오버레이 서비스 시작/중지 토글. 권한 없으면 권한 화면으로 유도.</summary>
    private void ToggleOverlay()
    {
        if (Services.OverlayService.IsRunning)
        {
            StopService(new Intent(this, typeof(Services.OverlayService)));
        }
        else
        {
            if (!global::Android.Provider.Settings.CanDrawOverlays(this))
            {
                Toast.MakeText(this, "먼저 '오버레이 권한 허용'을 눌러 주세요.", ToastLength.Long)?.Show();
                RequestOverlayPermission();
                return;
            }
            var intent = new Intent(this, typeof(Services.OverlayService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(intent);
            else StartService(intent);
        }
        UpdateOverlayControls();
    }

    /// <summary>오버레이 권한 상태 문구 + 토글 버튼 라벨을 현재 상태로 갱신.</summary>
    private void UpdateOverlayControls()
    {
        if (_overlayPermissionText is not null)
        {
            _overlayPermissionText.Text = global::Android.Provider.Settings.CanDrawOverlays(this)
                ? "다른 앱 위 표시: 허용됨 ✓"
                : "다른 앱 위 표시: 미허용 — '오버레이 권한 허용'을 눌러 설정에서 켜 주세요.";
        }
        if (_overlayToggleButton is not null)
            _overlayToggleButton.Text = Services.OverlayService.IsRunning
                ? "가사 오버레이 끄기" : "가사 오버레이 켜기";
    }

    private void RenderStatus()
    {
        UpdateOverlayControls();

        var source = MusebaseApp.Instance?.Source;
        if (source is null || _permissionText is null || _statusText is null) return;

        var granted = source.HasNotificationAccess;
        _permissionText.Text = granted
            ? "알림 접근: 허용됨 ✓"
            : "알림 접근: 미허용 — 아래 버튼으로 설정에서 Musebase를 켜 주세요.";

        if (!granted)
        {
            _statusText.Text = "감지 불가 (알림 접근 권한 필요)";
            return;
        }

        var track = source.CurrentTrack;
        if (track is null)
        {
            _statusText.Text = "감지된 미디어 세션 없음\n(음악 앱에서 재생을 시작해 보세요)";
            return;
        }

        var position = source.GetEstimatedPosition();
        _statusText.Text =
            $"곡명: {track.Title}\n" +
            $"아티스트: {track.Artist}\n" +
            $"앨범: {track.Album}\n" +
            $"위치: {Format(position)} / {Format(track.Duration)}\n" +
            $"상태: {(source.IsPlaying ? "재생 중" : "일시정지")}\n" +
            $"소스 앱: {track.SourceAppId}";
    }

    private static string Format(TimeSpan? t) =>
        t is { } v ? $"{(int)v.TotalMinutes}:{v.Seconds:00}" : "-:--";

    protected override void OnDestroy()
    {
        _handler.RemoveCallbacksAndMessages(null);
        if (MusebaseApp.Instance is { } app)
        {
            if (_onStateChanged is not null) app.Coordinator.StateChanged -= _onStateChanged;
            if (_onStatusChanged is not null) app.Coordinator.StatusChanged -= _onStatusChanged;
        }
        _onStateChanged = null;
        _onStatusChanged = null;
        base.OnDestroy();
    }
}
