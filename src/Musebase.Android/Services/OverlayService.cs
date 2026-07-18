using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Musebase.Android.Views;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// 다른 앱 위에 떠서 실시간 가사를 보여주는 포그라운드 서비스.
///
/// 하는 일:
/// 1) <see cref="WindowManager"/>에 <c>TYPE_APPLICATION_OVERLAY</c> 뷰(화면 하단 중앙, 반투명
///    둥근 배경 + 현재 줄 카라오케 + 번역)를 붙인다. 터치는 완전 통과
///    (<c>FLAG_NOT_FOCUSABLE | FLAG_NOT_TOUCHABLE</c>).
/// 2) <see cref="MusebaseApp"/>이 이미 1회 조립한 <see cref="LyricsCoordinator"/>를 구독만 한다
///    (엔진을 다시 만들지 않는다 — 골든룰/재사용). <c>CurrentLineChanged</c>로 줄·번역·타임태그를,
///    <c>LineProgressChanged</c>로 라인 경과(초)를 받아 글자 단위로 채운다
///    (<see cref="KaraokeTextView"/>). 재생/일시정지는 <see cref="AndroidNowPlayingSource"/>의
///    <c>IsPlayingChanged</c>로 받아 자연스럽게 표시/숨김.
/// 3) Android 8+ 필수인 포그라운드 알림(채널 + "가사 표시 중" + 정지 액션)을 띄운다.
///
/// 모든 뷰 조작·이벤트는 메인 스레드에서 일어난다(서비스 콜백 스레드 = 메인, 코디네이터
/// 이벤트는 <see cref="AndroidEngineDispatcher"/>가 메인으로 정렬, 소스 이벤트도 메인 핸들러).
/// </summary>
[Service(
    Label = "Musebase 가사 오버레이",
    Name = "com.countnine.musebase.OverlayService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class OverlayService : Service
{
    public const string ActionStop = "com.countnine.musebase.action.STOP_OVERLAY";
    private const string ChannelId = "musebase_overlay";
    private const int NotificationId = 0x0B45; // "OB" 느낌의 임의 상수

    /// <summary>서비스 실행 여부(MainActivity 토글 라벨용).</summary>
    public static bool IsRunning { get; private set; }

    private IWindowManager? _windowManager;
    private View? _overlayView;
    private KaraokeTextView? _lineView;
    private TextView? _translationView;

    private LyricsCoordinator? _coordinator;
    private AndroidNowPlayingSource? _source;

    // 구독 해제용 델리게이트 보관.
    private Action<DisplayLine?>? _onLineChanged;
    private Action<double>? _onProgress;
    private Action<bool>? _onPlayingChanged;

    private bool _hasLine;
    private bool _isPlaying;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelfCleanly();
            return StartCommandResult.NotSticky;
        }

        // 포그라운드 승격은 시작 직후(짧은 창) 안에 해야 한다.
        StartForeground(NotificationId, BuildNotification());

        // 오버레이 권한이 없으면(회수 등) 뷰를 붙일 수 없다 — 서비스 종료.
        if (!global::Android.Provider.Settings.CanDrawOverlays(this))
        {
            global::Android.Util.Log.Warn("Musebase", "overlay: SYSTEM_ALERT_WINDOW not granted — stopping service.");
            StopSelfCleanly();
            return StartCommandResult.NotSticky;
        }

        if (_overlayView is null) AttachOverlay();
        IsRunning = true;
        return StartCommandResult.Sticky;
    }

    // ---- 오버레이 뷰 구성 ----

    private void AttachOverlay()
    {
        _windowManager = GetSystemService(WindowService)?.JavaCast<IWindowManager>();
        if (_windowManager is null) { StopSelfCleanly(); return; }

        var metrics = Resources!.DisplayMetrics!;
        var density = metrics.Density;
        int Dp(float dp) => (int)(dp * density + 0.5f);
        var maxTextWidth = metrics.WidthPixels - Dp(48);

        // 반투명 둥근 배경 카드.
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetGravity(GravityFlags.Center);
        card.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
        var bg = new GradientDrawable();
        bg.SetCornerRadius(Dp(18));
        bg.SetColor(Color.Argb(0xB4, 0, 0, 0)); // ~70% 검정
        card.Background = bg;

        _lineView = new KaraokeTextView(this, textSizeSp: 22f, maxWidthPx: maxTextWidth);
        card.AddView(_lineView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });

        _translationView = new TextView(this)
        {
            Text = "",
            Visibility = ViewStates.Gone,
        };
        _translationView.SetTextColor(Color.Argb(0xFF, 0xE8, 0xE8, 0xE8));
        _translationView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15f);
        _translationView.SetShadowLayer(5f, 0f, 1.5f, Color.Argb(0xC8, 0, 0, 0));
        _translationView.Gravity = GravityFlags.Center;
        var trParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center, TopMargin = Dp(4) };
        card.AddView(_translationView, trParams);

        _overlayView = card;
        _overlayView.Visibility = ViewStates.Gone; // 재생/라인 확인 전까지 숨김

        var lp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            // 포커스·터치 모두 받지 않아 아래 앱으로 완전 통과(디스플레이 전용 오버레이).
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable,
            Format.Translucent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            Y = Dp(80), // 하단에서 위로 여백
        };

        _windowManager.AddView(_overlayView, lp);
        SubscribeEngine();
    }

    // ---- 엔진 구독(재사용 — 재조립 금지) ----

    private void SubscribeEngine()
    {
        if (MusebaseApp.Instance is not { } app) return;
        _coordinator = app.Coordinator;
        _source = app.Source;

        _isPlaying = _source.IsPlaying;

        _onLineChanged = OnLineChanged;
        _onProgress = OnProgress;
        _onPlayingChanged = OnPlayingChanged;

        _coordinator.CurrentLineChanged += _onLineChanged;
        _coordinator.LineProgressChanged += _onProgress;
        _source.IsPlayingChanged += _onPlayingChanged;

        // 현재 라인을 즉시 다시 발행받아 초기 표시(다음 틱에 CurrentLineChanged 재발화).
        _coordinator.RefreshCurrentLine();
        _lineView?.SetAnimating(_isPlaying);
    }

    private void OnLineChanged(DisplayLine? line)
    {
        var content = line?.Content;
        _hasLine = !string.IsNullOrEmpty(content);

        _lineView?.SetLine(content, line?.Karaoke, line?.LineSpanSeconds ?? 0);

        if (_translationView is not null)
        {
            var tr = line?.Translation;
            if (string.IsNullOrEmpty(tr))
            {
                _translationView.Visibility = ViewStates.Gone;
            }
            else
            {
                _translationView.Text = tr;
                _translationView.Visibility = ViewStates.Visible;
            }
        }
        UpdateVisibility();
    }

    private void OnProgress(double elapsedSeconds) => _lineView?.SetElapsed(elapsedSeconds);

    private void OnPlayingChanged(bool playing)
    {
        _isPlaying = playing;
        _lineView?.SetAnimating(playing);
        UpdateVisibility();
    }

    /// <summary>재생 중 + 표시할 라인이 있을 때만 오버레이를 보인다(일시정지·무곡 시 숨김).</summary>
    private void UpdateVisibility()
    {
        if (_overlayView is null) return;
        _overlayView.Visibility = _hasLine && _isPlaying ? ViewStates.Visible : ViewStates.Gone;
    }

    // ---- 알림(포그라운드) ----

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var channel = new NotificationChannel(
            ChannelId, "Musebase 가사 오버레이", NotificationImportance.Low)
        {
            Description = "다른 앱 위에 가사를 표시하는 동안 유지되는 알림",
        };
        channel.SetShowBadge(false);
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        nm?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var stopIntent = new Intent(this, typeof(OverlayService)).SetAction(ActionStop);
        var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
            ? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
            : PendingIntentFlags.UpdateCurrent;
        var stopPending = PendingIntent.GetService(this, 1, stopIntent, flags);

        // int-아이콘 Action.Builder는 API 23에서 폐기 — Icon 오버로드 사용.
        var stopIcon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMenuCloseClearCancel);

        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Musebase 가사 표시 중")
            .SetContentText("다른 앱 위에 실시간 가사를 표시합니다")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetOngoing(true)
            .SetVisibility(NotificationVisibility.Public)
            .AddAction(new Notification.Action.Builder(stopIcon, "정지", stopPending).Build());

        return builder.Build();
    }

    // ---- 정리 ----

    private void StopSelfCleanly()
    {
        UnsubscribeEngine();
        RemoveOverlay();
        IsRunning = false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
            StopForeground(StopForegroundFlags.Remove);
        else
#pragma warning disable CA1422 // 구형(API<24) 폴백
            StopForeground(true);
#pragma warning restore CA1422
        StopSelf();
    }

    private void UnsubscribeEngine()
    {
        if (_coordinator is not null)
        {
            if (_onLineChanged is not null) _coordinator.CurrentLineChanged -= _onLineChanged;
            if (_onProgress is not null) _coordinator.LineProgressChanged -= _onProgress;
        }
        if (_source is not null && _onPlayingChanged is not null)
            _source.IsPlayingChanged -= _onPlayingChanged;

        _onLineChanged = null;
        _onProgress = null;
        _onPlayingChanged = null;
        _coordinator = null;
        _source = null;
    }

    private void RemoveOverlay()
    {
        if (_windowManager is not null && _overlayView is not null)
        {
            try { _windowManager.RemoveView(_overlayView); }
            catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"overlay remove: {e.Message}"); }
        }
        _overlayView = null;
        _lineView = null;
        _translationView = null;
        _windowManager = null;
    }

    public override void OnDestroy()
    {
        UnsubscribeEngine();
        RemoveOverlay();
        IsRunning = false;
        base.OnDestroy();
    }
}
