using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Musebase.Core;
using Musebase.Engine;

namespace Musebase.Android;

/// <summary>
/// 메인 화면 — **전체 가사를 보여 주는 스크롤 뷰**(Apple Music 가사처럼 현재 줄을 강조하고
/// 자동으로 가운데로 스크롤) + 하단 **재생 컨트롤**(이전/재생·정지/다음). 나머지 기능
/// (설정·오버레이 토글·권한·종료)은 상단 아이콘 줄로 접어 화면을 가사에 내준다.
///
/// 하는 일:
/// 1) 상단 아이콘 바: 오버레이 켜기/끄기, 오버레이 위치 이동, 설정, 앱 종료
/// 2) 권한 배너: 필요한 권한이 빠졌을 때만 나타난다(허용되면 사라져 공간을 돌려준다)
/// 3) 가사: <see cref="LyricsCoordinator.CurrentLyrics"/>의 전 줄을 그리고, 현재 줄만 흰색·굵게.
///    현재 줄은 <see cref="Lyrics.LineIndexesAt"/>로 재생 위치에서 직접 계산한다(엔진 변경 없음).
/// 4) 재생 컨트롤: <see cref="INowPlayingSource"/>의 컨트롤 API를 그대로 쓴다(가용 여부에 따라 흐림).
///
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
    /// <summary>가사 진행·권한 표시 갱신 주기(현재 줄 추적이 있어 1초보다 촘촘하게).</summary>
    private const int UiRefreshMs = 300;

    /// <summary>알림 권한(Android 13+) 요청 코드.</summary>
    private const int RequestPostNotifications = 0x9001;

    /// <summary>
    /// 알림 표시 권한 이름. <c>Manifest.Permission.PostNotifications</c> 상수는 API 33 전용이라
    /// (SupportedOSPlatformVersion 26에서 CA1416) 문자열로 직접 쓴다 — 값은 동일하다.
    /// </summary>
    private const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";

    private static readonly Color ActiveColor = Color.White;
    private static readonly Color InactiveColor = Color.Argb(0xFF, 0x8A, 0x8A, 0x8A);
    private static readonly Color TranslationColor = Color.Argb(0xFF, 0xC8, 0xC8, 0xC8);

    private readonly Handler _handler = new(Looper.MainLooper!);

    private TextView? _trackText;
    private TextView? _statusText;
    private TextView? _permissionBanner;
    private ImageButton? _overlayButton;
    private ImageButton? _moveButton;
    private ImageButton? _playPauseButton;
    private ImageButton? _prevButton;
    private ImageButton? _nextButton;
    private ScrollView? _lyricsScroll;
    private LinearLayout? _lyricsColumn;
    private TextView? _emptyLyricsText;

    private bool _uiLoopRunning;

    // 현재 그려 둔 가사(같은 곡이면 다시 만들지 않는다)와 줄별 뷰.
    private Lyrics? _renderedLyrics;
    private readonly List<TextView> _lineViews = new();
    // 줄별 번역 뷰 — 번역이 나중에 채워지면(엔진이 같은 Lyrics를 갱신) 여기만 다시 칠한다.
    private readonly List<TextView> _translationViews = new();
    private int _activeLineIndex = -1;
    private bool _userScrolling; // 사용자가 직접 스크롤 중이면 자동 스크롤을 잠시 멈춘다
    private long _userScrollUntilMs;

    private Action<LyricsStatus>? _onStatusChanged;
    private Action<TranslationDisplayStatus>? _onTranslationStatusChanged;
    private Action<TrackInfo?>? _onTrackChanged;
    private LyricsStatus _lastLyricsStatus = new(LyricsStatusKind.NoTrack);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Argb(0xFF, 0x11, 0x11, 0x11));
        root.SetPadding(Dp(16), Dp(20), Dp(16), Dp(8));

        // ---- 상단 우측: 기능 아이콘 바(상태표시줄 아래 빈 영역) ----
        var iconBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        iconBar.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);

        // ---- 곡 정보 ----
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);

        var titleColumn = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _trackText = new TextView(this) { Text = "재생 중인 곡 없음" };
        _trackText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16f);
        _trackText.SetTextColor(ActiveColor);
        _trackText.SetSingleLine(true);
        _trackText.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        titleColumn.AddView(_trackText);

        _statusText = new TextView(this) { Text = "가사 대기 중" };
        _statusText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _statusText.SetTextColor(InactiveColor);
        _statusText.SetSingleLine(true);
        _statusText.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        titleColumn.AddView(_statusText);

        header.AddView(titleColumn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _overlayButton = IconButton(global::Android.Resource.Drawable.IcMenuView, "가사 오버레이", ToggleOverlay);
        _moveButton = IconButton(global::Android.Resource.Drawable.IcMenuCompass, "오버레이 위치 이동", ToggleOverlayMoveMode);
        var searchButton = IconButton(global::Android.Resource.Drawable.IcMenuSearch, "가사 검색",
            () => StartActivity(new Intent(this, typeof(SearchActivity))));
        var meaningButton = IconButton(global::Android.Resource.Drawable.IcMenuInfoDetails, "이 곡의 의미", ShowMeaning);
        var wrongButton = IconButton(global::Android.Resource.Drawable.IcMenuDelete, "틀린 가사로 표시", ConfirmMarkWrong);
        var settingsButton = IconButton(global::Android.Resource.Drawable.IcMenuPreferences, "설정",
            () => StartActivity(new Intent(this, typeof(SettingsActivity))));
        var quitButton = IconButton(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "앱 종료", ConfirmQuit);
        iconBar.AddView(searchButton);
        iconBar.AddView(meaningButton);
        iconBar.AddView(wrongButton);
        iconBar.AddView(_overlayButton);
        iconBar.AddView(_moveButton);
        iconBar.AddView(settingsButton);
        iconBar.AddView(quitButton);

        // 아이콘 줄을 곡 정보 위(우상단 빈 영역)에 둔다 — 곡명이 길어도 아이콘과 다투지 않는다.
        root.AddView(iconBar, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(header);

        // ---- 권한 배너(빠진 권한이 있을 때만) ----
        _permissionBanner = new TextView(this) { Visibility = ViewStates.Gone };
        _permissionBanner.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13f);
        _permissionBanner.SetTextColor(Color.Argb(0xFF, 0xFF, 0xD5, 0x4F));
        _permissionBanner.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        _permissionBanner.Click += (_, _) => RequestNextMissingPermission();
        var bannerParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) };
        root.AddView(_permissionBanner, bannerParams);

        // ---- 가사(전체 스크롤) ----
        _lyricsColumn = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _lyricsColumn.SetGravity(GravityFlags.CenterHorizontal);
        _lyricsColumn.SetPadding(0, Dp(24), 0, Dp(24));

        _emptyLyricsText = new TextView(this) { Text = "♪", Gravity = GravityFlags.Center };
        _emptyLyricsText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
        _emptyLyricsText.SetTextColor(InactiveColor);
        _lyricsColumn.AddView(_emptyLyricsText);

        _lyricsScroll = new ScrollView(this) { FillViewport = true };
        _lyricsScroll.AddView(_lyricsColumn, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        // 사용자가 직접 스크롤하면 몇 초간 자동 스크롤을 양보한다(읽는 중에 끌려가지 않도록).
        _lyricsScroll.Touch += (_, e) =>
        {
            if (e.Event?.Action is MotionEventActions.Down or MotionEventActions.Move)
            {
                _userScrolling = true;
                _userScrollUntilMs = SystemClock.UptimeMillis() + 5000;
            }
            e.Handled = false;
        };
        root.AddView(_lyricsScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // ---- 하단: 재생 컨트롤 ----
        var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        controls.SetGravity(GravityFlags.Center);
        controls.SetPadding(0, Dp(8), 0, Dp(8));
        _prevButton = IconButton(global::Android.Resource.Drawable.IcMediaPrevious, "이전 곡",
            () => Control(s => s.SkipPreviousAsync()), sizeDp: 56);
        _playPauseButton = IconButton(global::Android.Resource.Drawable.IcMediaPlay, "재생/일시정지",
            () => Control(s => s.TogglePlayPauseAsync()), sizeDp: 64);
        _nextButton = IconButton(global::Android.Resource.Drawable.IcMediaNext, "다음 곡",
            () => Control(s => s.SkipNextAsync()), sizeDp: 56);
        controls.AddView(_prevButton);
        controls.AddView(_playPauseButton);
        controls.AddView(_nextButton);
        root.AddView(controls);

        SetContentView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // ---- 엔진 구독 (조립은 MusebaseApp이 이미 완료) ----
        if (MusebaseApp.Instance is { } app)
        {
            // 종료(A안)로 리스너 바인드를 끊어 둔 상태였다면 여기서 복구한다.
            app.ResumeAfterQuit();

            _onStatusChanged = s => { _lastLyricsStatus = s; RenderStatusLine(); RenderLyrics(); };
            // 번역이 끝나면 화면의 가사도 즉시 번역본으로 바꿔 준다(같은 곡 = 뷰 재생성 없이 글자만).
            _onTranslationStatusChanged = _ => { RenderStatusLine(); RenderLyrics(); RefreshTranslations(); };
            _onTrackChanged = _ => { RenderTrack(); RenderLyrics(); };
            app.Coordinator.StatusChanged += _onStatusChanged;
            app.Coordinator.TranslationStatusChanged += _onTranslationStatusChanged;
            app.Source.TrackChanged += _onTrackChanged;

            _lastLyricsStatus = app.LastStatus;
            RenderTrack();
            RenderStatusLine();
            RenderLyrics();
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

    // ---- 주기 갱신 ----

    private void UiTick()
    {
        if (!_uiLoopRunning) return;
        UpdatePermissionBanner();
        UpdateControlButtons();
        RenderLyrics();
        RefreshTranslations(); // 줄 단위로 번역이 채워지는 경우까지 놓치지 않는다(바뀐 줄만 손댄다)
        UpdateActiveLine();
        _handler.PostDelayed(UiTick, UiRefreshMs);
    }

    // ---- 가사 ----

    /// <summary>곡이 바뀌었거나 가사가 새로 들어왔을 때만 줄 뷰를 다시 만든다.</summary>
    private void RenderLyrics()
    {
        var lyrics = MusebaseApp.Instance?.Coordinator.CurrentLyrics;
        if (ReferenceEquals(lyrics, _renderedLyrics)) return;
        _renderedLyrics = lyrics;
        _activeLineIndex = -1;

        if (_lyricsColumn is null || _emptyLyricsText is null) return;
        _lyricsColumn.RemoveAllViews();
        _lineViews.Clear();
        _translationViews.Clear();

        if (lyrics is null || lyrics.Lines.Count == 0)
        {
            _emptyLyricsText.Text = Services.StatusText.Lyrics(_lastLyricsStatus) is { Length: > 0 } s ? s : "♪";
            _lyricsColumn.AddView(_emptyLyricsText);
            return;
        }

        foreach (var line in lyrics.Lines)
        {
            var block = new LinearLayout(this) { Orientation = Orientation.Vertical };
            block.SetPadding(Dp(8), Dp(10), Dp(8), Dp(10));

            var original = new TextView(this)
            {
                Text = string.IsNullOrWhiteSpace(line.Content) ? "♪" : line.Content,
                Gravity = GravityFlags.Center,
            };
            original.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 19f);
            original.SetTextColor(InactiveColor);
            block.AddView(original);

            // 번역 줄은 처음엔 비어 있을 수 있다(번역이 나중에 도착) — 뷰는 미리 만들어 두고
            // 채워지는 시점에 RefreshTranslations()가 글자만 넣는다.
            var translation = ResolveTranslation(line);
            var tr = new TextView(this)
            {
                Text = translation ?? "",
                Gravity = GravityFlags.Center,
                Visibility = string.IsNullOrWhiteSpace(translation) ? ViewStates.Gone : ViewStates.Visible,
            };
            tr.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
            tr.SetTextColor(TranslationColor);
            tr.SetPadding(0, Dp(2), 0, 0);
            block.AddView(tr);

            _lyricsColumn.AddView(block, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _lineViews.Add(original); // 강조·스크롤 기준은 원문 줄
            _translationViews.Add(tr);
        }
    }

    /// <summary>
    /// 번역이 채워졌을 때(엔진이 같은 <see cref="Lyrics"/> 객체의 줄에 태그를 붙인다) 번역 줄만 갱신한다.
    /// 곡이 바뀐 게 아니므로 뷰를 다시 만들지 않고 글자만 넣어, 스크롤 위치·강조가 튀지 않는다.
    /// </summary>
    private void RefreshTranslations()
    {
        if (_renderedLyrics is not { } lyrics) return;
        var count = Math.Min(_translationViews.Count, lyrics.Lines.Count);
        for (var i = 0; i < count; i++)
        {
            var text = ResolveTranslation(lyrics.Lines[i]);
            var view = _translationViews[i];
            var empty = string.IsNullOrWhiteSpace(text);
            if (!empty && view.Text == text) continue; // 바뀐 것만 손댄다
            view.Text = text ?? "";
            view.Visibility = empty ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    /// <summary>
    /// 표시할 번역 — **오버레이(플로팅)와 똑같은 규칙**을 쓴다(`LyricsCoordinator.ResolveDisplayTranslation`).
    /// 대상이 중국어면 제공자 번역(중국어)을 그대로, "대상 언어 번역만 표시"가 켜져 있으면 대상 태그만,
    /// 꺼져 있으면 대상 → 제공자 순. 이 규칙이 없으면 제공자의 중국어 번역이 그대로 떠 버린다.
    /// </summary>
    private static string? ResolveTranslation(LyricsLine line)
    {
        if (MusebaseApp.Instance?.Coordinator is not { } coordinator) return null;
        var target = (coordinator.TargetLanguage ?? "").ToLowerInvariant();
        var att = line.Attachments;
        if (target.StartsWith("zh", StringComparison.Ordinal)) return att.Translation(null, target);
        if (coordinator.ShowOnlyTargetTranslation) return att.Translation(target);
        return att.Translation(target, null);
    }

    /// <summary>
    /// 재생 위치로 현재 줄을 찾아 강조하고, 화면 가운데로 부드럽게 스크롤한다
    /// (사용자가 직접 스크롤한 직후에는 양보한다).
    /// </summary>
    private void UpdateActiveLine()
    {
        if (MusebaseApp.Instance is not { } app || _renderedLyrics is not { } lyrics) return;
        if (_lineViews.Count == 0 || _lyricsScroll is null) return;

        var position = app.Source.GetEstimatedPosition();
        if (position is null) return;

        var adjusted = position.Value.TotalSeconds + lyrics.TimeDelay + app.Coordinator.ManualOffsetSeconds;
        var (current, _) = lyrics.LineIndexesAt(adjusted);
        var index = current ?? -1;
        if (index == _activeLineIndex) return;

        if (_activeLineIndex >= 0 && _activeLineIndex < _lineViews.Count)
        {
            var previous = _lineViews[_activeLineIndex];
            previous.SetTextColor(InactiveColor);
            previous.SetTypeface(Typeface.Default, TypefaceStyle.Normal);
        }

        _activeLineIndex = index;
        if (index < 0 || index >= _lineViews.Count) return;

        var view = _lineViews[index];
        view.SetTextColor(ActiveColor);
        view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);

        if (_userScrolling && SystemClock.UptimeMillis() < _userScrollUntilMs) return;
        _userScrolling = false;

        // 현재 줄이 화면 가운데 오도록(뷰 최상단 기준 y - 화면 높이 절반 + 줄 높이 절반).
        var target = ((View?)view.Parent)?.Top ?? view.Top;
        var y = Math.Max(0, target - _lyricsScroll.Height / 2 + view.Height);
        _lyricsScroll.SmoothScrollTo(0, y);
    }

    // ---- 상단/컨트롤 ----

    private void RenderTrack()
    {
        if (_trackText is null) return;
        var track = MusebaseApp.Instance?.Source.CurrentTrack;
        _trackText.Text = Services.StatusText.Track(track) ?? "재생 중인 곡 없음";
    }

    private void RenderStatusLine()
    {
        if (_statusText is null) return;
        _statusText.Text = Services.StatusText.Combined(
            _lastLyricsStatus,
            MusebaseApp.Instance?.Coordinator.CurrentTranslationStatus ?? TranslationDisplayStatus.None);
    }

    private void UpdateControlButtons()
    {
        RenderTrack();
        var source = MusebaseApp.Instance?.Source;
        if (source is null) return;

        var controls = source.GetControls();
        SetEnabled(_prevButton, controls.CanPrevious);
        SetEnabled(_nextButton, controls.CanNext);
        SetEnabled(_playPauseButton, controls.CanPlayPause);
        _playPauseButton?.SetImageResource(source.IsPlaying
            ? global::Android.Resource.Drawable.IcMediaPause
            : global::Android.Resource.Drawable.IcMediaPlay);

        // 오버레이 버튼은 켜져 있을 때 밝게 — 상태를 아이콘 하나로 알린다.
        if (_overlayButton is not null)
            _overlayButton.Alpha = Services.OverlayService.IsRunning ? 1f : 0.45f;
        if (_moveButton is not null)
            _moveButton.Alpha = Services.OverlayService.IsRunning ? 1f : 0.3f;
    }

    private static void SetEnabled(ImageButton? button, bool enabled)
    {
        if (button is null) return;
        button.Enabled = enabled;
        button.Alpha = enabled ? 1f : 0.3f;
    }

    private void Control(Func<INowPlayingSource, Task<bool>> action)
    {
        if (MusebaseApp.Instance?.Source is not { } source) return;
        _ = action(source);
    }

    // ---- 권한 ----

    /// <summary>빠진 권한이 있을 때만 배너를 띄운다(다 갖춰지면 사라져 가사에 공간을 준다).</summary>
    private void UpdatePermissionBanner()
    {
        if (_permissionBanner is null) return;
        var source = MusebaseApp.Instance?.Source;

        string? message = null;
        if (source is not null && !source.HasNotificationAccess)
            message = "알림 접근 권한이 필요합니다 — 눌러서 설정 열기 (재생 감지)";
        else if (!global::Android.Provider.Settings.CanDrawOverlays(this))
            message = "다른 앱 위에 표시 권한이 없습니다 — 눌러서 허용 (가사 오버레이)";
        else if (!HasNotificationPermission)
            message = "알림 표시 권한이 없습니다 — 눌러서 허용 (곡명·번역 상태 알림)";

        _permissionBanner.Text = message ?? "";
        _permissionBanner.Visibility = message is null ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>배너를 누르면 지금 빠진 권한 하나를 요청한다(위 순서대로).</summary>
    private void RequestNextMissingPermission()
    {
        var source = MusebaseApp.Instance?.Source;
        if (source is not null && !source.HasNotificationAccess)
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionNotificationListenerSettings));
            return;
        }
        if (!global::Android.Provider.Settings.CanDrawOverlays(this)) { RequestOverlayPermission(); return; }
        if (!HasNotificationPermission) RequestNotificationPermission();
    }

    /// <summary>알림 표시 권한이 있는지(Android 12 이하는 항상 true).</summary>
    private bool HasNotificationPermission =>
        Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu
        || CheckSelfPermission(PostNotificationsPermission)
           == global::Android.Content.PM.Permission.Granted;

    /// <summary>
    /// 알림 표시 권한 요청(Android 13+). 거부돼 있으면 시스템 대화상자를 띄우고,
    /// "다시 묻지 않음"으로 막힌 경우를 대비해 앱 알림 설정 화면으로 유도한다.
    /// </summary>
    private void RequestNotificationPermission()
    {
        if (HasNotificationPermission) return;
        if (ShouldShowRequestPermissionRationale(PostNotificationsPermission))
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionAppNotificationSettings)
                .PutExtra(global::Android.Provider.Settings.ExtraAppPackage, PackageName));
            return;
        }
        RequestPermissions(new[] { PostNotificationsPermission }, RequestPostNotifications);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, global::Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestPostNotifications) return;

        UpdatePermissionBanner();
        if (!HasNotificationPermission)
        {
            Toast.MakeText(this,
                "알림을 허용하지 않으면 곡명·번역 상태 알림이 표시되지 않습니다(오버레이는 정상 동작).",
                ToastLength.Long)?.Show();
            return;
        }

        // 권한을 이제 받았는데 서비스가 이미 떠 있으면, 알림이 뜨도록 서비스를 한 번 깨운다.
        if (Services.OverlayService.IsRunning) StartOverlayService(null);
    }

    private void RequestOverlayPermission()
    {
        if (global::Android.Provider.Settings.CanDrawOverlays(this)) return;
        StartActivity(new Intent(
            global::Android.Provider.Settings.ActionManageOverlayPermission,
            global::Android.Net.Uri.Parse("package:" + PackageName)));
    }

    // ---- 오버레이 / 종료 ----

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
                Toast.MakeText(this, "먼저 '다른 앱 위에 표시' 권한을 허용해 주세요.", ToastLength.Long)?.Show();
                RequestOverlayPermission();
                return;
            }
            // 알림 권한이 없으면 서비스는 돌지만 곡명·상태 알림이 표시되지 않는다 → 먼저 요청.
            if (!HasNotificationPermission) RequestNotificationPermission();
            StartOverlayService(null);
        }
        UpdateControlButtons();
    }

    /// <summary>오버레이 위치 이동 모드 토글(알림바의 "위치 이동"과 같은 동작).</summary>
    private void ToggleOverlayMoveMode()
    {
        if (!Services.OverlayService.IsRunning)
        {
            Toast.MakeText(this, "먼저 가사 오버레이를 켜 주세요.", ToastLength.Short)?.Show();
            return;
        }
        StartOverlayService(Services.OverlayService.ActionToggleMove);
    }

    private void StartOverlayService(string? action)
    {
        var intent = new Intent(this, typeof(Services.OverlayService));
        if (action is not null) intent.SetAction(action);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(intent);
        else StartService(intent);
    }

    /// <summary>
    /// 현재 곡을 "틀린 가사"로 표시한다(Windows 트레이의 같은 기능) — 이 곡은 이후 가사를 찾지 않고,
    /// 캐시에 저장된 잘못된 가사도 지운다. 되돌리려면 가사 검색에서 직접 골라 적용하면 된다.
    /// </summary>
    /// <summary>
    /// "이 곡의 의미" — 가사 서버가 미리 만들어 둔 문단을 <b>읽기만 한다</b>.
    /// 생성은 서버 관리자 화면에서만 일어나므로 앱은 조회 전용이고, 없으면 그렇게 알려 줄 뿐이다.
    ///
    /// 출처 표기는 의무라(Wikipedia CC BY-SA 등) 본문과 함께 반드시 붙인다.
    /// </summary>
    private async void ShowMeaning()
    {
        if (MusebaseApp.Instance is not { } app || app.Source.CurrentTrack is not { } track)
        {
            Toast.MakeText(this, "재생 중인 곡이 없습니다.", ToastLength.Short)?.Show();
            return;
        }

        if (app.Coordinator.RemoteCache is not { } remote)
        {
            Toast.MakeText(this, "가사 서버가 설정되지 않았습니다.", ToastLength.Short)?.Show();
            return;
        }

        var loading = new AlertDialog.Builder(this)
            .SetTitle($"{track.Title} — {track.Artist}")!
            .SetMessage("불러오는 중…")!
            .SetCancelable(true)!
            .Show();

        Musebase.Core.Search.SongMeaningView? meaning = null;
        try { meaning = await remote.GetMeaningAsync(track.Title, track.Artist); }
        catch (Exception) { /* 조용한 강등 — 부가 기능이다 */ }

        loading?.Dismiss();
        if (IsFinishing || IsDestroyed) return;

        var body = meaning is null
            // 대부분의 곡에는 아직 의미가 없다 — 실패가 아니라 정상이다.
            ? "이 곡의 의미는 아직 없습니다."
            : meaning.Summary + "\n\n" + meaning.CreditLine;

        new AlertDialog.Builder(this)
            .SetTitle($"{track.Title} — {track.Artist}")!
            .SetMessage(body)!
            .SetPositiveButton("닫기", (_, _) => { })!
            .Show();
    }

    private void ConfirmMarkWrong()
    {
        if (MusebaseApp.Instance?.Source.CurrentTrack is null)
        {
            Toast.MakeText(this, "재생 중인 곡이 없습니다.", ToastLength.Short)?.Show();
            return;
        }

        new AlertDialog.Builder(this)
            .SetTitle("틀린 가사로 표시")!
            .SetMessage("이 곡의 가사를 지우고 앞으로 표시하지 않습니다.\n다시 보려면 '가사 검색'에서 직접 골라 적용하세요.")!
            .SetPositiveButton("표시", (_, _) =>
            {
                MusebaseApp.Instance?.Coordinator.MarkWrongLyrics();
                Toast.MakeText(this, "틀린 가사로 표시했습니다.", ToastLength.Short)?.Show();
            })!
            .SetNegativeButton("취소", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    /// <summary>
    /// 앱 완전 종료(확인 후) — 오버레이 중지 + 재생 감지 중지 + 알림 리스너 바인드 해제.
    /// 권한 설정은 유지되므로 앱을 다시 열면 자동으로 복구된다.
    /// </summary>
    private void ConfirmQuit()
    {
        new AlertDialog.Builder(this)
            .SetTitle("Musebase 종료")!
            .SetMessage("오버레이와 재생 감지를 모두 멈추고 앱을 종료합니다.\n앱을 다시 열면 그대로 복구됩니다(권한 재설정 불필요).")!
            .SetPositiveButton("종료", (_, _) =>
            {
                MusebaseApp.Instance?.QuitCompletely();
                FinishAndRemoveTask();
            })!
            .SetNegativeButton("취소", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    // ---- 유틸 ----

    private int Dp(float dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    private ImageButton IconButton(int iconRes, string description, Action onClick, float sizeDp = 44)
    {
        var button = new ImageButton(this) { ContentDescription = description };
        button.SetImageResource(iconRes);
        button.SetBackgroundColor(Color.Transparent);
        button.SetColorFilter(ActiveColor);
        button.Click += (_, _) => onClick();
        var size = Dp(sizeDp);
        button.LayoutParameters = new LinearLayout.LayoutParams(size, size) { LeftMargin = Dp(4) };
        return button;
    }

    protected override void OnDestroy()
    {
        _handler.RemoveCallbacksAndMessages(null);
        if (MusebaseApp.Instance is { } app)
        {
            if (_onStatusChanged is not null) app.Coordinator.StatusChanged -= _onStatusChanged;
            if (_onTranslationStatusChanged is not null) app.Coordinator.TranslationStatusChanged -= _onTranslationStatusChanged;
            if (_onTrackChanged is not null) app.Source.TrackChanged -= _onTrackChanged;
        }
        _onStatusChanged = null;
        _onTranslationStatusChanged = null;
        _onTrackChanged = null;
        base.OnDestroy();
    }
}
