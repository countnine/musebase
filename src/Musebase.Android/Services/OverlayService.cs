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
///    둥근 배경 + 현재 줄 카라오케 + 번역)를 붙인다. 평소 터치는 완전 통과
///    (<c>FLAG_NOT_FOCUSABLE | FLAG_NOT_TOUCHABLE</c>) — 아래 앱 조작을 막지 않는다.
///    기본 위치는 제스처 바·내비게이션 바 높이(WindowInsets)를 피해 그 위에 잡는다.
///    "이동 모드"(알림 액션)에서는 잠시 터치를 받아 드래그로 옮길 수 있고, 위치는 화면 대비
///    비율로 저장돼 회전·해상도 변화에도 화면 밖으로 나가지 않는다.
/// 1-b) 버블(플로팅) 모드: 밴드 대신 작은 원형 버블만 띄우고, 탭하면 밴드를 펼친다
///    (다시 탭하면 접힘). 버블은 드래그로 옮길 수 있고 놓으면 가까운 좌/우 가장자리에 붙는다.
///    접힌 상태에서도 새 가사 줄이 나오면 잠깐 펼쳤다 접는 peek 옵션이 있다.
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
    /// <summary>알림바에서 API 번역을 켜고 끄는 액션(유료 사용량을 그 자리에서 끊기 위한 단축키).</summary>
    public const string ActionToggleTranslation = "com.countnine.musebase.action.TOGGLE_TRANSLATION";
    /// <summary>오버레이 위치 이동 모드 토글(켜는 동안만 오버레이가 터치를 받는다).</summary>
    public const string ActionToggleMove = "com.countnine.musebase.action.TOGGLE_MOVE";
    /// <summary>표시 방식 설정(버블 모드 등) 변경을 서비스에 반영시키는 액션.</summary>
    public const string ActionRefreshDisplay = "com.countnine.musebase.action.REFRESH_DISPLAY";
    private const string ChannelId = "musebase_overlay";
    private const int NotificationId = 0x0B45; // "OB" 느낌의 임의 상수
    private const int PeekMs = 3000;           // 접힌 상태에서 새 줄을 잠깐 보여 주는 시간
    private const float BubbleSizeDp = 56f;    // 버블 지름(머티리얼 FAB과 같은 크기)
    private const float PocketSizeDp = 72f;    // 하단 포켓(놓으면 오버레이 종료) 지름
    private const float PocketBottomDp = 48f;  // 포켓을 화면 하단에서 띄우는 여백(+시스템 인셋)
    private const int FadeMs = 220;            // 밴드 페이드 인/아웃 시간(Windows 오버레이와 같은 감각)

    /// <summary>서비스 실행 여부(MainActivity 토글 라벨용).</summary>
    public static bool IsRunning { get; private set; }

    private IWindowManager? _windowManager;
    private View? _overlayView;
    private KaraokeTextView? _lineView;
    private TextView? _translationView;

    // 밴드/버블 창의 레이아웃 파라미터(드래그·위치 복원 시 갱신해 UpdateViewLayout에 넘긴다).
    private WindowManagerLayoutParams? _bandLp;
    private WindowManagerLayoutParams? _bubbleLp;
    private View? _bubbleView;
    private TextView? _bubbleLabel;
    private BubbleStatusDrawable? _bubbleBackground;

    // 버블 길게 누르기로 여는 퀵 메뉴(앱 열기 / API 번역 / 위치 이동)와 그 항목 라벨.
    private LinearLayout? _menuView;
    private WindowManagerLayoutParams? _menuLp;
    private TextView? _menuTranslationItem;
    private TextView? _menuMoveItem;

    // 버블을 끌 때만 나타나는 하단 "포켓"(여기에 놓으면 오버레이를 끈다).
    private TextView? _pocketView;
    private GradientDrawable? _pocketBackground;
    private WindowManagerLayoutParams? _pocketLp;

    private readonly Handler _handler = new(Looper.MainLooper!);
    // 예약 취소(RemoveCallbacks)가 확실히 같은 대상을 가리키도록 Runnable 인스턴스를 보관한다
    // (Action 오버로드는 호출마다 래퍼가 새로 생겨 취소가 빗나갈 수 있다).
    private Java.Lang.IRunnable? _peekCollapse;
    private Java.Lang.IRunnable? _longPress;

    private bool _moveMode;              // 이동 모드(밴드가 터치를 받는 동안만 true)
    private bool _bubbleMode;            // 버블 모드(설정에서 켬)
    private bool _bandExpanded = true;   // 버블 모드에서 밴드를 펼쳐 두었는지
    private bool _bandPositionPending;   // 저장된 위치를 아직 적용하지 못했다(뷰 크기 확정 전)
    private int _screenWidth, _screenHeight;

    private LyricsCoordinator? _coordinator;
    private AndroidNowPlayingSource? _source;

    // 구독 해제용 델리게이트 보관.
    private Action<DisplayLine?>? _onLineChanged;
    private Action<double>? _onProgress;
    private Action<bool>? _onPlayingChanged;
    private Action<LyricsStatus>? _onStatusChanged;
    private Action<TranslationDisplayStatus>? _onTranslationStatusChanged;
    private Action<TrackInfo?>? _onTrackChanged;

    // 알림바에 표시 중인 상태(곡/가사 상태) — 이벤트마다 최신값으로 갱신한다.
    private LyricsStatus? _lastStatus;

    private bool _hasLine;
    private bool _isPlaying;
    private bool _bandVisible;        // 가사 밴드가 지금 실제로 보이는가(버블 채움 표기의 근거)
    private string? _lastLineContent; // 같은 줄 재발행과 진짜 새 줄을 구분(peek 트리거용)

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

        if (intent?.Action == ActionToggleTranslation)
        {
            ToggleApiTranslation();
            return StartCommandResult.Sticky;
        }

        if (intent?.Action == ActionToggleMove)
        {
            SetMoveMode(!_moveMode);
            return StartCommandResult.Sticky;
        }

        if (intent?.Action == ActionRefreshDisplay)
        {
            ApplyDisplayMode();
            return StartCommandResult.Sticky;
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

        _bandLp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            // 포커스·터치 모두 받지 않아 아래 앱으로 완전 통과(디스플레이 전용 오버레이).
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable,
            Format.Translucent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            // 제스처 바·내비게이션 바에 겹쳐 가려지지 않도록 그 높이만큼 더 띄운다.
            Y = Dp(24) + BottomInsetPx(),
        };

        _windowManager.AddView(_overlayView, _bandLp);
        // 저장된 사용자 위치가 있으면 뷰 크기가 확정된 뒤(=처음 보일 때) 적용한다.
        _bandPositionPending = HasSavedRatio(MusebaseApp.Instance?.Settings.OverlayRatio);
        // 인셋은 창이 붙은 뒤에야 읽을 수 있다 — 붙자마자 실제 값으로 기본 여백을 다시 잡는다.
        _overlayView.Post(ApplyDefaultBottomOffset);
        AttachDragHandler(_overlayView, isBubble: false);

        SubscribeEngine();
        ApplyDisplayMode();
    }

    /// <summary>
    /// 설정의 오버레이 스타일(색·크기·배경·모서리)을 현재 뷰에 적용한다.
    /// Windows 설정 [오버레이] 탭과 같은 항목·기본값을 쓴다.
    /// </summary>
    private void ApplyOverlayStyle()
    {
        if (MusebaseApp.Instance is not { } app || _overlayView is null) return;
        var s = app.Settings;
        var density = Resources!.DisplayMetrics!.Density;
        int Dp(float dp) => (int)(dp * density + 0.5f);

        var textColor = AndroidSettings.ParseColor(s.OverlayTextColor, KaraokeTextView.DefaultBaseColor);
        var fillColor = AndroidSettings.ParseColor(s.OverlayKaraokeColor, KaraokeTextView.DefaultFillColor);
        _lineView?.SetColors(textColor, fillColor);
        _lineView?.SetTextSizeSp(s.OverlayFontSizeSp);

        if (_translationView is not null)
        {
            _translationView.SetTextColor(
                AndroidSettings.ParseColor(s.OverlayTranslationColor, Color.Argb(0xFF, 0xE8, 0xE8, 0xE8)));
            // 번역 줄은 원문 크기에 비례(Windows와 같은 감각: 대략 0.68배).
            _translationView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, s.OverlayFontSizeSp * 0.68f);
        }

        if (_overlayView.Background is GradientDrawable bg)
        {
            bg.SetCornerRadius(Dp(s.OverlayCornerRadius));
            if (s.OverlayBackgroundEnabled)
            {
                var color = AndroidSettings.ParseColor(s.OverlayBackgroundColor, Color.Black);
                bg.SetColor(Color.Argb((int)(Math.Clamp(s.OverlayBackgroundOpacity, 0f, 1f) * 255),
                    color.R, color.G, color.B));
            }
            else
            {
                bg.SetColor(Color.Transparent);
            }
        }
    }

    /// <summary>설정(버블 모드)을 읽어 버블/밴드 구성을 현재 상태에 맞춘다.</summary>
    private void ApplyDisplayMode()
    {
        ApplyOverlayStyle();
        _bubbleMode = MusebaseApp.Instance?.Settings.OverlayBubbleMode ?? false;
        if (_bubbleMode)
        {
            _bandExpanded = false; // 버블 모드로 들어오면 접힌 상태에서 시작
            if (_bubbleView is null) AttachBubble();
        }
        else
        {
            _bandExpanded = true;
            RemoveBubble();
        }
        UpdateVisibility();
        UpdateNotification();
    }

    // ---- 버블(플로팅) ----

    private void AttachBubble()
    {
        if (_windowManager is null) return;
        var density = Resources!.DisplayMetrics!.Density;
        int Dp(float dp) => (int)(dp * density + 0.5f);
        var size = Dp(BubbleSizeDp);

        _bubbleBackground = new BubbleStatusDrawable(density);

        _bubbleLabel = new TextView(this) { Text = "♪", Gravity = GravityFlags.Center };
        _bubbleLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
        _bubbleLabel.SetTextColor(Color.White);
        _bubbleLabel.Background = _bubbleBackground;

        _bubbleView = _bubbleLabel;
        RefreshScreenSize();

        var (rx, ry) = MusebaseApp.Instance?.Settings.BubbleRatio ?? (AndroidSettings.UnsetRatio, AndroidSettings.UnsetRatio);
        var maxX = Math.Max(1, _screenWidth - size);
        var maxY = Math.Max(1, _screenHeight - size);
        _bubbleLp = new WindowManagerLayoutParams(
            size, size,
            WindowManagerTypes.ApplicationOverlay,
            // 버블은 탭·드래그를 받아야 하므로 NotTouchable을 주지 않는다(포커스는 받지 않음).
            WindowManagerFlags.NotFocusable,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = HasSavedRatio((rx, ry)) ? (int)(rx * maxX) : maxX - Dp(8),
            Y = HasSavedRatio((rx, ry)) ? (int)(ry * maxY) : (int)(maxY * 0.65f),
        };

        _windowManager.AddView(_bubbleView, _bubbleLp);
        AttachDragHandler(_bubbleView, isBubble: true);
        UpdateBubbleAppearance();
    }

    private void RemoveBubble()
    {
        _bubbleBackground?.Stop(); // 회전 틱이 남지 않도록 뷰를 떼기 전에 멈춘다
        if (_windowManager is not null && _bubbleView is not null)
        {
            try { _windowManager.RemoveView(_bubbleView); }
            catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"bubble remove: {e.Message}"); }
        }
        _bubbleView = null;
        _bubbleLabel = null;
        _bubbleBackground = null;
        _bubbleLp = null;
    }

    /// <summary>
    /// 버블 하나에 세 가지를 싣는다.
    /// ① 테두리 링 = 가사 상태(검색 중 회전 / 찾음 밝게 / 못 찾음 회색),
    /// ② 안쪽 채움 = 오버레이가 지금 보이는지(보이면 링 색으로 채우고 음표를 반전),
    /// ③ 우상단 점 = 번역 예외(한도·실패=주황, API 꺼짐=회색). 정상이면 점 없음.
    /// </summary>
    private void UpdateBubbleAppearance()
    {
        if (_bubbleBackground is null) return;
        var settings = MusebaseApp.Instance?.Settings;

        var state = (_lastStatus ?? _coordinator?.CurrentStatus)?.Kind switch
        {
            LyricsStatusKind.Searching => BubbleLyricsState.Searching,
            LyricsStatusKind.Found or LyricsStatusKind.Cache
                or LyricsStatusKind.Manual or LyricsStatusKind.Edited => BubbleLyricsState.Found,
            _ => BubbleLyricsState.Missing, // NotFound·Wrong·HiddenByUser·NoTrack·미발행
        };

        var translation = _coordinator?.CurrentTranslationStatus ?? TranslationDisplayStatus.None;
        Color? badge = translation switch
        {
            TranslationDisplayStatus.Quota or TranslationDisplayStatus.Failed
                => Color.Argb(0xFF, 0xFF, 0xA7, 0x26),
            // 껐지만 캐시로 다 채워진 경우(DisabledCached)는 표시가 정상이므로 점을 찍지 않는다.
            TranslationDisplayStatus.Disabled => Color.Argb(0xFF, 0x9E, 0x9E, 0x9E),
            _ => null,
        };

        _bubbleBackground.SetStatus(
            state,
            _bandVisible,
            AndroidSettings.ParseColor(settings?.OverlayTextColor, Color.White),
            AndroidSettings.ParseColor(settings?.OverlayKaraokeColor, KaraokeTextView.DefaultFillColor),
            badge);

        if (_bubbleLabel is not null) _bubbleLabel.SetTextColor(_bubbleBackground.GlyphColor);
        if (_bubbleView is not null)
        {
            // 곡이 없을 때만 살짝 흐리게 — 그 외에는 상태를 또렷하게 읽히도록 완전 불투명.
            _bubbleView.Alpha = state == BubbleLyricsState.Missing && !_isPlaying ? 0.8f : 1f;
            _bubbleView.ContentDescription = state switch
            {
                BubbleLyricsState.Searching => "가사 찾는 중",
                BubbleLyricsState.Found => _bandVisible ? "가사 표시 중" : "가사 있음 — 탭하면 표시",
                _ => "가사 없음",
            };
        }
    }

    /// <summary>버블 탭 — 가사 밴드를 펼치거나 접는다.</summary>
    private void ToggleBand()
    {
        _bandExpanded = !_bandExpanded;
        CancelPeek();
        UpdateVisibility();
        UpdateBubbleAppearance();
    }

    // ---- 드래그(이동) ----

    /// <summary>
    /// 드래그로 창을 옮기는 공통 터치 처리. 밴드는 이동 모드에서만 터치를 받고(평소 통과),
    /// 버블은 항상 받는다. 손가락을 뗄 때 위치를 비율로 저장하고, 버블은 가까운 가장자리에 붙인다.
    /// </summary>
    private void AttachDragHandler(View view, bool isBubble)
    {
        var slop = ViewConfiguration.Get(this)?.ScaledTouchSlop ?? 16;
        float downX = 0, downY = 0;
        int startX = 0, startY = 0;
        var dragged = false;
        var longPressed = false; // 길게 눌러 앱을 연 경우 손을 뗄 때 펼치기/접기를 하지 않는다

        view.Touch += (_, e) =>
        {
            var ev = e.Event;
            var lp = isBubble ? _bubbleLp : _bandLp;
            if (ev is null || lp is null || _windowManager is null) { e.Handled = false; return; }

            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    downX = ev.RawX; downY = ev.RawY;
                    startX = lp.X; startY = lp.Y;
                    dragged = false;
                    longPressed = false;
                    // 버블을 길게 누르면 퀵 메뉴를 연다(짧게 탭 = 펼치기/접기).
                    if (isBubble) ScheduleLongPress(view, () => longPressed = true);
                    e.Handled = true;
                    break;

                case MotionEventActions.Move:
                    var dx = ev.RawX - downX;
                    var dy = ev.RawY - downY;
                    if (Math.Abs(dx) > slop || Math.Abs(dy) > slop) dragged = true;
                    if (dragged)
                    {
                        CancelLongPress(); // 끌기 시작 = 길게 누르기 아님
                        if (isBubble)
                        {
                            HideQuickMenu();
                            ShowPocket(); // 끌기 시작 → 하단에 놓기 대상 표시
                        }
                        // 드래그 중에는 절대 좌표계로 다뤄야 하므로 기준점을 고정한다.
                        EnsureAbsoluteGravity(view, lp, isBubble);
                        // 세로 모드의 밴드는 좌우로 움직이지 않는다(가운데 고정) — 높이만 조절.
                        if (!CenterHorizontally(isBubble)) lp.X = startX + (int)dx;
                        lp.Y = startY + (int)dy;
                        ClampToScreen(view, lp);
                        try { _windowManager.UpdateViewLayout(view, lp); } catch (Exception) { /* 창이 이미 떨어짐 */ }
                        if (isBubble) HighlightPocket(IsBubbleOverPocket()); // 포켓 위면 빨갛게
                    }
                    e.Handled = true;
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    CancelLongPress();
                    // 포켓에 놓았으면 오버레이를 끈다(버블·밴드·알림 모두 사라진다).
                    if (isBubble && dragged && IsBubbleOverPocket())
                    {
                        HidePocket();
                        Toast.MakeText(this, "오버레이를 껐습니다 — 앱에서 다시 켤 수 있습니다", ToastLength.Long)?.Show();
                        StopSelfCleanly();
                        e.Handled = true;
                        break;
                    }
                    if (isBubble) HidePocket();
                    if (!dragged && !longPressed && isBubble) ToggleBand();
                    else if (dragged)
                    {
                        if (isBubble) SnapBubbleToEdge();
                        SavePosition(view, lp, isBubble);
                    }
                    e.Handled = true;
                    break;

                default:
                    e.Handled = false;
                    break;
            }
        };
    }

    // ---- 퀵 메뉴(버블 길게 누르기) ----

    /// <summary>
    /// 버블 옆에 뜨는 작은 메뉴 — 자주 쓰는 동작을 한 번에 고르게 한다.
    /// 앱 열기 / API 번역 사용(현재 상태 표시·토글) / 오버레이 위치 이동(현재 상태 표시·토글).
    /// 메뉴 밖을 누르면 닫힌다(<c>WatchOutsideTouch</c>).
    /// </summary>
    private void ShowQuickMenu()
    {
        if (_windowManager is null || _bubbleView is null || _bubbleLp is null) return;
        HideQuickMenu();

        var density = Resources!.DisplayMetrics!.Density;
        int Dp(float dp) => (int)(dp * density + 0.5f);

        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var bg = new GradientDrawable();
        bg.SetCornerRadius(Dp(14));
        bg.SetColor(Color.Argb(0xF2, 0x1E, 0x1E, 0x1E));
        bg.SetStroke(Dp(1), Color.Argb(0x59, 0xFF, 0xFF, 0xFF));
        card.Background = bg;
        card.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));

        TextView Item(string text, Action onClick)
        {
            var tv = new TextView(this) { Text = text };
            tv.SetTextColor(Color.White);
            tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15f);
            tv.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
            tv.Click += (_, _) => onClick();
            card.AddView(tv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            return tv;
        }

        Item("앱 열기", () => { HideQuickMenu(); OpenAppScreen(); });
        _menuTranslationItem = Item(TranslationMenuLabel(), () =>
        {
            ToggleApiTranslation();
            RefreshQuickMenuLabels(); // 메뉴를 열어 둔 채 바뀐 상태를 바로 보여 준다
        });
        _menuMoveItem = Item(MoveMenuLabel(), () =>
        {
            HideQuickMenu(); // 위치를 잡으려면 화면이 비어야 한다
            SetMoveMode(!_moveMode);
        });

        // 앱 완전 종료 — 실수로 누르는 일이 없도록 한 번 더 탭해야 실행된다
        // (서비스에서 대화상자를 띄우는 대신 라벨로 확인을 받는다).
        TextView? quitItem = null;
        var quitArmed = false;
        quitItem = Item("앱 종료", () =>
        {
            if (!quitArmed)
            {
                quitArmed = true;
                if (quitItem is not null) quitItem.Text = "한 번 더 누르면 종료";
                return;
            }
            HideQuickMenu();
            MusebaseApp.Instance?.QuitCompletely(); // 오버레이 중지 + 감지 중지 + 리스너 언바인드
        });

        _menuView = card;
        card.Measure(
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        var w = card.MeasuredWidth;
        var h = card.MeasuredHeight;

        RefreshScreenSize();
        var gap = Dp(8);
        var bubbleW = _bubbleView.Width > 0 ? _bubbleView.Width : _bubbleLp.Width;
        var bubbleH = _bubbleView.Height > 0 ? _bubbleView.Height : _bubbleLp.Height;
        // 버블이 왼쪽 가장자리에 붙어 있으면 오른쪽에, 아니면 왼쪽에 편다.
        var onLeft = _bubbleLp.X + bubbleW / 2 < _screenWidth / 2;
        var x = onLeft ? _bubbleLp.X + bubbleW + gap : _bubbleLp.X - w - gap;
        var y = _bubbleLp.Y + bubbleH / 2 - h / 2;

        _menuLp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            // 바깥 터치를 알림받아 닫되(WatchOutsideTouch), 키보드 포커스는 가져가지 않는다.
            WindowManagerFlags.NotFocusable | WindowManagerFlags.WatchOutsideTouch,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = Math.Max(0, Math.Min(x, Math.Max(0, _screenWidth - w))),
            Y = Math.Max(0, Math.Min(y, Math.Max(0, _screenHeight - h))),
        };

        card.Touch += (_, e) =>
        {
            if (e.Event?.Action == MotionEventActions.Outside) HideQuickMenu();
            e.Handled = false; // 항목 클릭은 그대로 전달
        };

        try { _windowManager.AddView(_menuView, _menuLp); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("Musebase", $"quick menu: {ex.Message}");
            _menuView = null;
        }
    }

    private void HideQuickMenu()
    {
        if (_windowManager is not null && _menuView is not null)
        {
            try { _windowManager.RemoveView(_menuView); }
            catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"quick menu remove: {e.Message}"); }
        }
        _menuView = null;
        _menuLp = null;
        _menuTranslationItem = null;
        _menuMoveItem = null;
    }

    private string TranslationMenuLabel() =>
        (MusebaseApp.Instance?.Settings.ApiTranslationEnabled ?? true)
            ? "API 번역 사용  ✓ 켬" : "API 번역 사용  ✕ 꺼짐";

    private string MoveMenuLabel() =>
        _moveMode ? "오버레이 위치 이동  ✓ 이동 중" : "오버레이 위치 이동";

    private void RefreshQuickMenuLabels()
    {
        if (_menuTranslationItem is not null) _menuTranslationItem.Text = TranslationMenuLabel();
        if (_menuMoveItem is not null) _menuMoveItem.Text = MoveMenuLabel();
    }

    // ---- 하단 포켓(버블을 놓으면 오버레이 종료) ----

    /// <summary>버블을 끌기 시작할 때 하단 중앙에 놓기 대상(✕)을 띄운다.</summary>
    private void ShowPocket()
    {
        if (_windowManager is null || _pocketView is not null) return;
        var density = Resources!.DisplayMetrics!.Density;
        int Dp(float dp) => (int)(dp * density + 0.5f);
        var size = Dp(PocketSizeDp);

        _pocketBackground = new GradientDrawable();
        _pocketBackground.SetShape(ShapeType.Oval);
        _pocketBackground.SetColor(Color.Argb(0xB4, 0x20, 0x20, 0x20));
        _pocketBackground.SetStroke(Dp(2), Color.Argb(0xC8, 0xFF, 0xFF, 0xFF));

        _pocketView = new TextView(this) { Text = "✕", Gravity = GravityFlags.Center };
        _pocketView.SetTextColor(Color.White);
        _pocketView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 22f);
        _pocketView.Background = _pocketBackground;

        _pocketLp = new WindowManagerLayoutParams(
            size, size,
            WindowManagerTypes.ApplicationOverlay,
            // 놓기 대상일 뿐이라 터치를 받지 않는다(드래그 중인 버블이 계속 이벤트를 받아야 한다).
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable,
            Format.Translucent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            Y = Dp(PocketBottomDp) + BottomInsetPx(),
        };

        try { _windowManager.AddView(_pocketView, _pocketLp); }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Musebase", $"pocket: {e.Message}");
            _pocketView = null;
        }
    }

    private void HidePocket()
    {
        if (_windowManager is not null && _pocketView is not null)
        {
            try { _windowManager.RemoveView(_pocketView); }
            catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"pocket remove: {e.Message}"); }
        }
        _pocketView = null;
        _pocketBackground = null;
        _pocketLp = null;
    }

    /// <summary>버블 중심이 포켓 위에 있는지(놓으면 종료). 겹칠 때 포켓을 강조한다.</summary>
    private bool IsBubbleOverPocket()
    {
        if (_pocketView is null || _pocketLp is null || _bubbleLp is null || _bubbleView is null) return false;
        RefreshScreenSize();
        var density = Resources!.DisplayMetrics!.Density;
        var pocketSize = _pocketLp.Width;
        var pocketCx = _screenWidth / 2f;
        var pocketCy = _screenHeight - _pocketLp.Y - pocketSize / 2f; // Gravity=Bottom 기준 → 화면 좌표로
        var bubbleSize = _bubbleView.Width > 0 ? _bubbleView.Width : _bubbleLp.Width;
        var bubbleCx = _bubbleLp.X + bubbleSize / 2f;
        var bubbleCy = _bubbleLp.Y + bubbleSize / 2f;

        var dx = bubbleCx - pocketCx;
        var dy = bubbleCy - pocketCy;
        var threshold = pocketSize / 2f + 12f * density; // 포켓 반경 + 약간의 여유
        return dx * dx + dy * dy <= threshold * threshold;
    }

    private void HighlightPocket(bool over)
    {
        if (_pocketBackground is null || _pocketView is null) return;
        var density = Resources!.DisplayMetrics!.Density;
        _pocketBackground.SetColor(over
            ? Color.Argb(0xE6, 0xD3, 0x2F, 0x2F)   // 겹치면 빨강 — 놓으면 꺼진다는 신호
            : Color.Argb(0xB4, 0x20, 0x20, 0x20));
        _pocketBackground.SetStroke((int)(2 * density + 0.5f), Color.Argb(0xC8, 0xFF, 0xFF, 0xFF));
        _pocketView.ScaleX = _pocketView.ScaleY = over ? 1.15f : 1f;
    }

    // ---- 길게 누르기(버블) → 퀵 메뉴 ----

    /// <summary>길게 누르기 예약. 시간 안에 손을 떼거나 끌기 시작하면 <see cref="CancelLongPress"/>로 취소된다.</summary>
    private void ScheduleLongPress(View view, Action onFired)
    {
        CancelLongPress();
        _longPress = new Java.Lang.Runnable(() =>
        {
            _longPress = null;
            onFired();
            view.PerformHapticFeedback(FeedbackConstants.LongPress);
            ShowQuickMenu();
        });
        _handler.PostDelayed(_longPress, ViewConfiguration.LongPressTimeout);
    }

    private void CancelLongPress()
    {
        if (_longPress is null) return;
        _handler.RemoveCallbacks(_longPress);
        _longPress = null;
    }

    /// <summary>
    /// 앱 화면(<see cref="MainActivity"/>)을 연다. 서비스에서 액티비티를 띄우므로 NewTask가 필요하고,
    /// 백그라운드 액티비티 실행 제한(Android 10+)은 오버레이 권한(SYSTEM_ALERT_WINDOW)을 가진 앱에는
    /// 적용되지 않는다 — 이 서비스는 그 권한이 있어야만 동작한다.
    /// </summary>
    private void OpenAppScreen()
    {
        try
        {
            var intent = new Intent(this, typeof(Musebase.Android.MainActivity))
                .SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            StartActivity(intent);
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Musebase", $"open app: {e.Message}");
        }
    }

    /// <summary>
    /// 세로 모드 여부. 세로에서는 가사 밴드가 화면 폭을 거의 다 쓰므로 좌우로 옮길 여지가 없다 —
    /// **항상 가로 가운데로 정렬**하고 사용자는 높이만 조절한다(가로 모드에서는 자유 배치).
    /// </summary>
    private bool IsPortrait =>
        Resources?.Configuration?.Orientation != global::Android.Content.Res.Orientation.Landscape;

    /// <summary>밴드가 가로 가운데 고정(세로 모드)이어야 하는지 — 버블은 항상 자유 배치.</summary>
    private bool CenterHorizontally(bool isBubble) => !isBubble && IsPortrait;

    /// <summary>
    /// 드래그를 위해 현재 화면 좌표를 기준점(좌상단, 세로 모드 밴드는 상단 중앙)으로 바꾼다.
    /// </summary>
    private void EnsureAbsoluteGravity(View view, WindowManagerLayoutParams lp, bool isBubble)
    {
        var target = CenterHorizontally(isBubble)
            ? GravityFlags.Top | GravityFlags.CenterHorizontal
            : GravityFlags.Top | GravityFlags.Left;
        if (lp.Gravity == target) return;

        RefreshScreenSize();
        var loc = new int[2];
        view.GetLocationOnScreen(loc);
        lp.Gravity = target;
        // CenterHorizontal에서 X는 중앙으로부터의 오프셋이므로 0 = 정중앙.
        lp.X = target.HasFlag(GravityFlags.CenterHorizontal) ? 0 : loc[0];
        lp.Y = loc[1];
    }

    private void ClampToScreen(View view, WindowManagerLayoutParams lp)
    {
        RefreshScreenSize();
        var w = view.Width > 0 ? view.Width : lp.Width;
        var h = view.Height > 0 ? view.Height : lp.Height;
        // 가운데 정렬 창은 X를 건드리지 않는다(0 = 정중앙).
        if (!lp.Gravity.HasFlag(GravityFlags.CenterHorizontal))
            lp.X = Math.Max(0, Math.Min(lp.X, Math.Max(0, _screenWidth - w)));
        lp.Y = Math.Max(0, Math.Min(lp.Y, Math.Max(0, _screenHeight - h)));
    }

    /// <summary>버블을 가까운 좌/우 가장자리에 붙인다(자석).</summary>
    private void SnapBubbleToEdge()
    {
        if (_bubbleView is null || _bubbleLp is null || _windowManager is null) return;
        RefreshScreenSize();
        var w = _bubbleView.Width > 0 ? _bubbleView.Width : _bubbleLp.Width;
        var margin = (int)(4 * Resources!.DisplayMetrics!.Density);
        var center = _bubbleLp.X + w / 2;
        _bubbleLp.X = center < _screenWidth / 2 ? margin : Math.Max(0, _screenWidth - w - margin);
        try { _windowManager.UpdateViewLayout(_bubbleView, _bubbleLp); } catch (Exception) { /* 무시 */ }
    }

    /// <summary>현재 위치를 화면 여유 공간 대비 비율로 저장한다(회전·해상도 변화 대응).</summary>
    private void SavePosition(View view, WindowManagerLayoutParams lp, bool isBubble)
    {
        if (MusebaseApp.Instance is not { } app) return;
        RefreshScreenSize();
        var w = view.Width > 0 ? view.Width : lp.Width;
        var h = view.Height > 0 ? view.Height : lp.Height;
        var maxX = Math.Max(1, _screenWidth - w);
        var maxY = Math.Max(1, _screenHeight - h);
        // 가운데 고정(세로 밴드)이면 X는 "중앙"을 뜻하는 0.5로 저장한다.
        var rx = CenterHorizontally(isBubble) ? 0.5f : (float)lp.X / maxX;
        var ratio = (rx, (float)lp.Y / maxY);
        if (isBubble) app.Settings.BubbleRatio = ratio;
        else app.Settings.OverlayRatio = ratio;
    }

    /// <summary>
    /// 기본 위치(하단 중앙)일 때 실제 시스템 바 인셋만큼 띄운다. 창이 붙은 뒤에야 인셋을 읽을 수 있어
    /// 부착 직후와 회전 시에 다시 호출한다. 사용자가 옮긴 뒤(절대 좌표)에는 건드리지 않는다.
    /// </summary>
    private void ApplyDefaultBottomOffset()
    {
        if (_overlayView is null || _bandLp is null || _windowManager is null) return;
        if (_bandLp.Gravity != (GravityFlags.Bottom | GravityFlags.CenterHorizontal)) return;

        var y = (int)(24 * Resources!.DisplayMetrics!.Density + 0.5f) + BottomInsetPx();
        if (y == _bandLp.Y) return;
        _bandLp.Y = y;
        try { _windowManager.UpdateViewLayout(_overlayView, _bandLp); } catch (Exception) { /* 무시 */ }
    }

    /// <summary>저장된 밴드 위치를 뷰 크기가 확정된 뒤 적용한다(처음 보일 때 1회).</summary>
    private void RestoreBandPositionIfPending()
    {
        if (!_bandPositionPending || _overlayView is null || _bandLp is null || _windowManager is null) return;
        if (_overlayView.Width <= 0 || _overlayView.Height <= 0) return; // 아직 레이아웃 전 — 다음 기회에
        var ratio = MusebaseApp.Instance?.Settings.OverlayRatio;
        if (!HasSavedRatio(ratio)) { _bandPositionPending = false; return; }

        RefreshScreenSize();
        var maxX = Math.Max(1, _screenWidth - _overlayView.Width);
        var maxY = Math.Max(1, _screenHeight - _overlayView.Height);
        // 세로 모드는 가로 가운데 고정, 가로 모드는 저장된 X 비율대로.
        if (IsPortrait)
        {
            _bandLp.Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal;
            _bandLp.X = 0;
        }
        else
        {
            _bandLp.Gravity = GravityFlags.Top | GravityFlags.Left;
            _bandLp.X = (int)(ratio!.Value.X * maxX);
        }
        _bandLp.Y = (int)(ratio!.Value.Y * maxY);
        ClampToScreen(_overlayView, _bandLp);
        try { _windowManager.UpdateViewLayout(_overlayView, _bandLp); } catch (Exception) { /* 무시 */ }
        _bandPositionPending = false;
    }

    private static bool HasSavedRatio((float X, float Y)? ratio) =>
        ratio is { } r && r.X >= 0f && r.Y >= 0f;

    private void RefreshScreenSize()
    {
        var metrics = Resources!.DisplayMetrics!;
        _screenWidth = metrics.WidthPixels;
        _screenHeight = metrics.HeightPixels;
    }

    /// <summary>
    /// 화면 아래쪽 시스템 영역(제스처 바·내비게이션 바) 높이. 기본 위치를 이 위로 띄워
    /// 밴드가 시스템 바에 가려지는 것을 막는다. 인셋을 못 얻으면 0(기존 여백만 적용).
    /// </summary>
    private int BottomInsetPx()
    {
        try
        {
            var insets = _overlayView?.RootWindowInsets;
            if (insets is null) return (int)(24 * Resources!.DisplayMetrics!.Density); // 붙이기 전 — 통상값
            // OperatingSystem.IsAndroidVersionAtLeast로 검사해야 CA1416 분석기가 가드를 인식한다.
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var types = WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout();
                return insets.GetInsets(types)?.Bottom ?? 0;
            }
#pragma warning disable CA1422 // API 30 미만 폴백(SystemWindowInsetBottom은 그 이전의 유일한 경로)
            return insets.SystemWindowInsetBottom;
#pragma warning restore CA1422
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Musebase", $"insets: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 이동 모드 토글. 켜면 밴드가 터치를 받아 드래그로 옮길 수 있고(그동안 아래 앱은 조작 불가),
    /// 끄면 다시 완전 통과로 돌아가며 위치를 저장한다.
    /// </summary>
    private void SetMoveMode(bool on)
    {
        if (_overlayView is null || _bandLp is null || _windowManager is null) return;
        _moveMode = on;
        if (on)
        {
            _bandLp.Flags = WindowManagerFlags.NotFocusable; // NotTouchable 해제 = 드래그 가능
            _bandExpanded = true;                            // 버블 모드여도 옮기려면 보여야 한다
            CancelPeek();
            // 재생 중이 아니면 빈 카드가 되어 잡을 곳이 없으므로 안내 문구를 넣어 준다.
            if (!_hasLine) _lineView?.SetLine("드래그해서 위치를 잡으세요", null, 0);
        }
        else
        {
            _bandLp.Flags = WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable;
            SavePosition(_overlayView, _bandLp, isBubble: false);
            if (_bubbleMode) _bandExpanded = false;
            _coordinator?.RefreshCurrentLine(); // 안내 문구 → 실제 가사로 복귀
        }

        UpdateVisibility();
        try { _windowManager.UpdateViewLayout(_overlayView, _bandLp); } catch (Exception) { /* 무시 */ }
        UpdateNotification();
        Toast.MakeText(this,
            on ? "이동 모드 — 가사를 끌어 원하는 위치에 놓으세요" : "위치를 저장했습니다",
            ToastLength.Short)?.Show();
    }

    // ---- peek(접힌 상태에서 잠깐 보여 주기) ----

    private void PeekIfCollapsed()
    {
        if (!_bubbleMode || _bandExpanded || _moveMode) return;
        if (!(MusebaseApp.Instance?.Settings.OverlayPeekOnNewLine ?? true)) return;
        CancelPeek();
        _bandExpanded = true;
        UpdateVisibility();
        _peekCollapse = new Java.Lang.Runnable(() =>
        {
            _bandExpanded = false;
            UpdateVisibility();
            UpdateBubbleAppearance();
            _peekCollapse = null;
        });
        _handler.PostDelayed(_peekCollapse, PeekMs);
    }

    private void CancelPeek()
    {
        if (_peekCollapse is null) return;
        _handler.RemoveCallbacks(_peekCollapse);
        _peekCollapse = null;
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
        // 알림바의 곡명·상태 갱신용 — 표시만 하고 엔진은 건드리지 않는다.
        // 가사 상태는 알림바 문구와 버블 링(검색 중 회전 / 찾음 / 못 찾음)을 함께 움직인다.
        _onStatusChanged = status => { _lastStatus = status; UpdateNotification(); UpdateBubbleAppearance(); };
        _onTranslationStatusChanged = _ => { UpdateNotification(); UpdateBubbleAppearance(); };
        // 곡이 바뀌면 이전 곡의 마지막 줄이 남아 떠 있지 않도록 카드를 비운다
        // (새 곡의 줄은 코디네이터가 곧 다시 발행한다).
        _onTrackChanged = _ =>
        {
            _hasLine = false;
            _lastLineContent = null;
            _lineView?.SetLine(null, null, 0);
            if (_translationView is not null) _translationView.Visibility = ViewStates.Gone;
            CancelPeek();
            if (_bubbleMode) _bandExpanded = false;
            UpdateVisibility();
            UpdateNotification();
        };

        _coordinator.CurrentLineChanged += _onLineChanged;
        _coordinator.LineProgressChanged += _onProgress;
        _coordinator.StatusChanged += _onStatusChanged;
        _coordinator.TranslationStatusChanged += _onTranslationStatusChanged;
        _source.IsPlayingChanged += _onPlayingChanged;
        _source.TrackChanged += _onTrackChanged;

        _lastStatus = _coordinator.CurrentStatus;

        // 현재 라인을 즉시 다시 발행받아 초기 표시(다음 틱에 CurrentLineChanged 재발화).
        _coordinator.RefreshCurrentLine();
        _lineView?.SetAnimating(_isPlaying);
        UpdateNotification();
    }

    private void OnLineChanged(DisplayLine? line)
    {
        // LRC의 간주 표시 줄(예: "[01:12.00] ")은 내용이 공백뿐이라 그대로 그리면
        // 글자 없는 작은 카드만 뜬다 — 빈 줄로 취급해 아예 숨긴다.
        var content = string.IsNullOrWhiteSpace(line?.Content) ? null : line!.Content;
        var isNew = content is not null && content != _lastLineContent;
        _lastLineContent = content;
        _hasLine = content is not null;

        // 글자 단위 카라오케를 끄면 타임태그를 넘기지 않아 줄 단위 채움으로 폴백한다(Windows와 동일).
        var karaoke = (MusebaseApp.Instance?.Settings.CharacterKaraoke ?? true) ? line?.Karaoke : null;
        _lineView?.SetLine(content, karaoke, line?.LineSpanSeconds ?? 0);

        if (_translationView is not null)
        {
            var tr = line?.Translation;
            if (string.IsNullOrWhiteSpace(tr))
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
        // 접혀 있어도 새 줄이 나오면 잠깐 보여 준다(설정으로 끌 수 있다).
        if (isNew && _isPlaying) PeekIfCollapsed();
    }

    private void OnProgress(double elapsedSeconds) => _lineView?.SetElapsed(elapsedSeconds);

    private void OnPlayingChanged(bool playing)
    {
        _isPlaying = playing;
        _lineView?.SetAnimating(playing);
        UpdateVisibility();
    }

    /// <summary>
    /// 재생 중 + 표시할 라인이 있을 때만 오버레이를 보인다(일시정지·무곡 시 숨김).
    /// 버블 모드에서는 펼친 상태(버블 탭 또는 peek)일 때만, 이동 모드에서는 항상 보인다.
    /// </summary>
    private void UpdateVisibility()
    {
        var show = _overlayView is not null
            && (_moveMode || (_hasLine && _isPlaying && (!_bubbleMode || _bandExpanded)));
        // 버블의 "표시 중" 표기는 _bandExpanded가 아니라 **실제로 보이는지**를 따라야 한다
        // (펼쳐 뒀어도 일시정지·무가사면 밴드는 보이지 않는다).
        _bandVisible = show;
        UpdateBubbleAppearance();

        if (_overlayView is null) return;
        SetBandVisible(show);
        // 저장된 위치는 뷰 크기가 정해진 뒤에만 적용할 수 있다(보이는 시점에 1회).
        if (show && _bandPositionPending) _overlayView.Post(RestoreBandPositionIfPending);
    }

    /// <summary>
    /// 밴드를 페이드로 보이고/숨긴다(설정으로 끄면 즉시 전환). 숨길 때는 애니메이션이 끝난 뒤
    /// Gone으로 바꿔야 창이 남지 않고, 도중에 다시 보이게 되면 진행 중 애니메이션을 취소한다.
    /// </summary>
    private void SetBandVisible(bool show)
    {
        if (_overlayView is not { } view) return;
        var wantFade = MusebaseApp.Instance?.Settings.OverlayFadeAnimation ?? true;
        var visible = view.Visibility == ViewStates.Visible;
        if (show == visible && (!show || view.Alpha >= 1f)) return;

        view.Animate()?.Cancel();
        if (!wantFade)
        {
            view.Alpha = 1f;
            view.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            return;
        }

        if (show)
        {
            view.Alpha = visible ? view.Alpha : 0f;
            view.Visibility = ViewStates.Visible;
            view.Animate()?.Alpha(1f)?.SetDuration(FadeMs)?.Start();
        }
        else
        {
            view.Animate()?.Alpha(0f)?.SetDuration(FadeMs)?.WithEndAction(new Java.Lang.Runnable(() =>
            {
                // 페이드 도중 다시 보이기로 바뀌었으면 숨기지 않는다.
                if (_overlayView is { } v && v.Alpha <= 0.01f) v.Visibility = ViewStates.Gone;
            }))?.Start();
        }
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
        var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
            ? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
            : PendingIntentFlags.UpdateCurrent;

        var stopIntent = new Intent(this, typeof(OverlayService)).SetAction(ActionStop);
        var stopPending = PendingIntent.GetService(this, 1, stopIntent, flags);
        var toggleIntent = new Intent(this, typeof(OverlayService)).SetAction(ActionToggleTranslation);
        var togglePending = PendingIntent.GetService(this, 2, toggleIntent, flags);
        var moveIntent = new Intent(this, typeof(OverlayService)).SetAction(ActionToggleMove);
        var movePending = PendingIntent.GetService(this, 3, moveIntent, flags);
        // 알림 본문을 터치하면 앱 화면이 열린다(액티비티이므로 GetActivity).
        var openIntent = new Intent(this, typeof(Musebase.Android.MainActivity))
            .SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        var openPending = PendingIntent.GetActivity(this, 4, openIntent, flags);

        // int-아이콘 Action.Builder는 API 23에서 폐기 — Icon 오버로드 사용.
        var stopIcon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMenuCloseClearCancel);
        var toggleIcon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMenuRotate);
        var moveIcon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMenuCompass);

        // 곡명이 있으면 제목 줄에, 아래 줄에 가사·번역 상태를 보여 준다.
        var track = StatusText.Track(_source?.CurrentTrack ?? _coordinator?.CurrentTrack);
        var status = StatusText.Combined(
            _lastStatus ?? _coordinator?.CurrentStatus,
            _coordinator?.CurrentTranslationStatus ?? TranslationDisplayStatus.None);

        var apiOn = MusebaseApp.Instance?.Settings.ApiTranslationEnabled ?? true;
        var toggleLabel = apiOn ? "번역 끄기" : "번역 켜기";

        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle(track ?? "Musebase 가사 표시 중")
            .SetContentText(string.IsNullOrEmpty(status) ? "다른 앱 위에 실시간 가사를 표시합니다" : status)
            .SetStyle(new Notification.BigTextStyle().BigText(
                (track is null ? "" : track + "\n") +
                (string.IsNullOrEmpty(status) ? "다른 앱 위에 실시간 가사를 표시합니다" : status)))
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetContentIntent(openPending)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true) // 곡이 바뀔 때마다 소리·진동으로 알리지 않는다
            .SetVisibility(NotificationVisibility.Public)
            .AddAction(new Notification.Action.Builder(toggleIcon, toggleLabel, togglePending).Build())
            .AddAction(new Notification.Action.Builder(
                moveIcon, _moveMode ? "이동 끝" : "위치 이동", movePending).Build())
            .AddAction(new Notification.Action.Builder(stopIcon, "정지", stopPending).Build());

        return builder.Build();
    }

    /// <summary>알림바 내용을 현재 곡·상태로 갱신한다(서비스가 떠 있을 때만).</summary>
    private void UpdateNotification()
    {
        // 이미 StartForeground로 떠 있는 알림을 갱신하는 것이므로 엔진 구독 여부만 본다.
        if (_coordinator is null) return;
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        try { nm?.Notify(NotificationId, BuildNotification()); }
        catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"notify: {e.Message}"); }
    }

    /// <summary>알림바 액션: API 번역 사용을 토글하고 즉시 반영한다(켤 때는 현재 곡을 바로 번역).</summary>
    private void ToggleApiTranslation()
    {
        if (MusebaseApp.Instance is not { } app) return;
        var next = !app.Settings.ApiTranslationEnabled;
        app.Settings.ApiTranslationEnabled = next;
        app.ApplyTranslationSettings(retranslateNow: next);
        global::Android.Util.Log.Info("Musebase", $"notification: API 번역 {(next ? "켬" : "끔")}");
        UpdateNotification();
        Toast.MakeText(this, next ? "API 번역 켬" : "API 번역 끔 — 캐시된 번역만 표시", ToastLength.Short)?.Show();
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
            if (_onStatusChanged is not null) _coordinator.StatusChanged -= _onStatusChanged;
            if (_onTranslationStatusChanged is not null)
                _coordinator.TranslationStatusChanged -= _onTranslationStatusChanged;
        }
        if (_source is not null)
        {
            if (_onPlayingChanged is not null) _source.IsPlayingChanged -= _onPlayingChanged;
            if (_onTrackChanged is not null) _source.TrackChanged -= _onTrackChanged;
        }

        _onLineChanged = null;
        _onProgress = null;
        _onPlayingChanged = null;
        _onStatusChanged = null;
        _onTranslationStatusChanged = null;
        _onTrackChanged = null;
        _lastStatus = null;
        _coordinator = null;
        _source = null;
    }

    private void RemoveOverlay()
    {
        CancelPeek();
        CancelLongPress();
        HideQuickMenu();
        HidePocket();
        RemoveBubble();
        if (_windowManager is not null && _overlayView is not null)
        {
            try { _windowManager.RemoveView(_overlayView); }
            catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"overlay remove: {e.Message}"); }
        }
        _overlayView = null;
        _lineView = null;
        _translationView = null;
        _bandLp = null;
        _windowManager = null;
    }

    /// <summary>
    /// 화면 회전·크기 변경 — 저장된 비율로 위치를 다시 계산한다(픽셀 좌표를 그대로 두면
    /// 가로/세로가 바뀔 때 화면 밖으로 나간다).
    /// </summary>
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (_windowManager is null) return;
        HideQuickMenu(); // 회전하면 버블 옆 좌표가 무의미해진다
        HidePocket();
        RefreshScreenSize();

        // 새 화면 폭에 맞춰 줄바꿈·가운데 정렬을 다시 잡는다(고정 폭이면 회전 후 어긋난다).
        _lineView?.SetMaxWidth(_screenWidth - (int)(48 * Resources!.DisplayMetrics!.Density + 0.5f));

        _bandPositionPending = HasSavedRatio(MusebaseApp.Instance?.Settings.OverlayRatio);
        UpdateVisibility();
        _overlayView?.Post(ApplyDefaultBottomOffset); // 회전하면 인셋 높이도 달라진다

        if (_bubbleView is not null && _bubbleLp is not null)
        {
            var (rx, ry) = MusebaseApp.Instance?.Settings.BubbleRatio
                ?? (AndroidSettings.UnsetRatio, AndroidSettings.UnsetRatio);
            if (HasSavedRatio((rx, ry)))
            {
                _bubbleLp.X = (int)(rx * Math.Max(1, _screenWidth - _bubbleLp.Width));
                _bubbleLp.Y = (int)(ry * Math.Max(1, _screenHeight - _bubbleLp.Height));
            }
            ClampToScreen(_bubbleView, _bubbleLp);
            try { _windowManager.UpdateViewLayout(_bubbleView, _bubbleLp); } catch (Exception) { /* 무시 */ }
        }
    }

    public override void OnDestroy()
    {
        UnsubscribeEngine();
        RemoveOverlay();
        IsRunning = false;
        base.OnDestroy();
    }
}
