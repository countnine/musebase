using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Musebase.Core.Search;
using Musebase.Engine;
using Musebase.Windows.Services;

namespace Musebase.Windows;

/// <summary>
/// 미니창(작업표시줄 상주)이 트레이·오버레이 없이 기본 기능을 쓰도록 주입받는 동작 묶음.
/// Program.cs의 트레이 핸들러와 <b>같은 로컬 함수</b>를 가리켜 중복 구현을 피한다.
/// </summary>
public sealed record MiniWindowActions(
    Func<bool> IsOverlayVisible,
    Action<bool> SetOverlayVisible,
    Action ReviveOverlay,
    Action OpenSettings,
    Action Exit,
    // 재생 컨트롤
    Func<PlaybackControls> GetControls,
    Func<bool> IsPlaying,
    Action OnPrevious,
    Action OnPlayPause,
    Action OnNext,
    // 가사 오프셋 (delta, null=리셋) / 현재값
    Action<double?> AdjustOffset,
    Func<double> GetOffset,
    // 가사 기능
    Action OpenSearch,
    Action OpenLyricsEditor,
    Action MarkWrong,
    Func<bool> HasLyrics,
    // 닫기 → 트레이 옵션(설정에서 실시간 조회)
    Func<bool> CloseToTray,
    // 곡에 딸린 것들(가사 서버) — 서버가 없으면 전부 null이고 그 자리는 조용히 비워진다
    Action? OpenMeaning = null,
    Func<Task<SongExtrasResult>>? GetExtras = null,
    Func<bool, Task<SongExtras?>>? SetLoved = null,
    Func<Task<SongExtras?>>? RefreshCover = null,
    // 재생 앱(SMTC)이 준 앨범 표지 — 서버 커버가 없을 때의 폴백
    Func<Task<byte[]?>>? GetThumbnail = null,
    // 의미: 있으면 창을 열고, 없으면 그 자리에서 만든다
    Func<Task<SongMeaningView?>>? GetMeaning = null,
    Func<Task<MeaningRequestResult>>? MakeMeaning = null,
    // 가사 서버 주소(설정값) — 이 곡의 서버 화면을 브라우저로 열 때 쓴다. 없으면 그 버튼이 사라진다
    Func<string?>? ServerEndpoint = null);

/// <summary>
/// 작업표시줄에 상주하는 컨트롤 허브. 오버레이가 숨겨져도(사용자 숨김·일시정지·가림방지)
/// 여기서 항상 되살릴 수 있는 손잡이 역할을 하며, 트레이 없이도 기본 기능을 쓸 수 있다.
///
/// <b>창 전체가 앨범 커버다.</b> 평소에는 커버와 곡명만 보이고, 마우스를 올리면 컨트롤이
/// 커버 위에 뜬다(lofi 플레이어와 같은 형태). 늘 띄워 두는 창이라 가만히 있을 때 조용한 쪽이
/// 낫고, 조작은 어차피 마우스를 올린 뒤에 한다.
///
/// - 곡 제목/아티스트 + 가사 소스 상태 표시.
/// - 재생 컨트롤(이전/재생·정지/다음), 오프셋 조정, 가사 검색/열기/틀린가사.
/// - 좋아요(Last.fm)·이 곡의 의미·커버 다시 찾기 — 가사 서버가 있을 때만.
/// - 오버레이 표시·설정·종료. 표시 상태·오프셋·컨트롤 활성은 트레이와 동기화.
/// - 닫기(X): 옵션 꺼짐(기본)=최소화(작업표시줄 상주), 켜짐=Hide()로 트레이로 숨김(실제 종료 아님).
/// </summary>
public sealed class MiniWindow : Window
{
    /// <summary>시작 크기. 커버는 정사각이라 창 한 변이 곧 커버 한 변이다.</summary>
    private const double ArtSize = 360;

    /// <summary>이보다 작아지면 버튼 줄이 접혀 쓸 수 없다(실측으로 잡은 하한).</summary>
    private const double MinSize = 260;

    /// <summary>이보다 커지면 늘 띄워 두는 창으로서 자리를 너무 차지한다.</summary>
    private const double MaxSize = 600;

    private static readonly Color Ink = Color.FromRgb(0xE7, 0xEA, 0xF0);
    private static readonly Color Dim = Color.FromRgb(0x8A, 0x93, 0xA2);

    /// <summary>아티스트처럼 <b>읽으라고 두는</b> 보조 글자. Dim은 상태·설명용이라 너무 흐리다.</summary>
    private static readonly Color Subtle = Color.FromRgb(0xC3, 0xCA, 0xD6);
    private static readonly Color Accent = Color.FromRgb(0x7C, 0xC4, 0xFF);
    private static readonly Color Heart = Color.FromRgb(0xFF, 0x8F, 0xB1);

    /// <summary>"모른다"·"안 됐다"를 나타내는 색. 켬/끔 어느 쪽으로도 읽히면 안 되므로 따로 둔다.</summary>
    private static readonly Color Warn = Color.FromRgb(0xFF, 0xC2, 0x6B);

    private readonly MiniWindowActions _a;

    private readonly Image _cover;
    private readonly Border _coverFallback;
    private readonly Grid _rest;
    private readonly Grid _veil;

    private readonly TextBlock _restTitle;
    private readonly TextBlock _restArtist;
    private readonly TextBlock _restMeaning;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _source;

    private readonly Button _prev;
    private readonly Button _playPause;
    private readonly Button _next;
    private readonly Button _offsetMinus;
    private readonly Button _offsetPlus;
    private readonly Button _offsetReset;
    private readonly TextBlock _offsetLabel;
    private readonly Button _search;
    private readonly Button _openLyrics;
    private readonly Button _wrong;
    private readonly Button _overlayToggle;
    private readonly Button _settingsButton;
    private readonly Button _exit;
    private readonly Button _love;
    private readonly Button _meaning;
    private readonly Button _coverRetry;
    private readonly Button _minimize;
    private readonly Button _close;
    private readonly Button _openServer;

    /// <summary>평상시 층 우상단의 좋아요 표시(누르는 것이 아니라 보는 것).</summary>
    private readonly TextBlock _restLove;

    private bool _closingToExit;   // "종료" 경로에서만 실제 닫힘 허용
    private SongExtras? _extras;
    private int _extrasEpoch;      // 곡이 바뀌면 늦게 도착한 응답을 버린다
    private bool _hasServerCover;  // 서버 커버를 걸었는가 — 재생 앱 표지로 덮지 않는다(그쪽이 더 선명하다)
    private bool _hasMeaning;      // 의미가 이미 있는가 — 버튼 글자가 이것으로 갈린다
    private bool _extrasFailed;    // 서버에 못 물어봤다 — "좋아요 없음"과 구별해 알린다
    private string? _fetchedKey;   // 마지막으로 조회한 곡(제목|아티스트) — 상태만 바뀌면 다시 안 받는다

    private readonly AppSettings _settings;

    /// <summary>평상시 층의 곡명 판 — 창 크기에 따라 너비가 바뀐다(FitTitle).</summary>
    private readonly Border _restPlate;

    private const double PlateMargin = 12;
    private const double PlatePadding = 10;

    /// <summary>우상단 최소화·닫기 버튼이 차지하는 폭(30×2 + 바깥 여백).</summary>
    private const double WindowButtonsWidth = 68;

    public MiniWindow(System.Drawing.Icon? appIcon, MiniWindowActions actions, AppSettings settings)
    {
        _a = actions;
        _settings = settings;

        Title = Loc.T("mini.title");

        // **제목 표시줄을 없앤다.** 두 가지를 한 번에 푼다 — ① 커버가 창 끝까지 닿아 타이틀바가
        // 커버를 가리지 않고, ② 창 크기와 내용 크기가 같아져 정사각이 정말 정사각이 된다.
        // 예전에는 타이틀바 몫을 추측해 더하다가 창이 세로로 길어졌다.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        // WindowStyle.None만으로는 창 위쪽에 DWM 유리 테두리가 흰 줄로 남는다.
        // GlassFrameThickness=0으로 그 줄을 없애고, 크기 조절은 테두리 두께로 계속 살려 둔다.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,                              // 제목 영역 없음(커버를 끌어 옮긴다)
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        MinWidth = MinHeight = MinSize;
        MaxWidth = MaxHeight = MaxSize;
        Width = Height = Math.Clamp(settings.PanelSize, MinSize, MaxSize);
        Topmost = true;             // 가사창과 같이 쓰는 창이라 뒤로 숨으면 쓸모가 없다
        ShowInTaskbar = true;
        // 위치는 직접 정한다 — CenterScreen은 매번 화면 한가운데로 되돌리고,
        // 작업영역이 아니라 화면 전체를 기준으로 잡아 작업표시줄을 고려하지 않는다.
        WindowStartupLocation = WindowStartupLocation.Manual;
        RestorePlacement();
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x13));

        if (appIcon is not null)
        {
            try
            {
                Icon = Imaging.CreateBitmapSourceFromHIcon(
                    appIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { /* 아이콘 변환 실패는 무시(기본 아이콘) */ }
        }

        // ---- 1) 커버(맨 아래 층) ----
        _cover = new Image { Stretch = Stretch.UniformToFill };
        // 커버가 없는 곡이 더 많다 — 빈 검정 판 대신 조용한 그라데이션을 깔아 창처럼 보이게 한다.
        _coverFallback = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(0x18, 0x1F, 0x2B), Color.FromRgb(0x0B, 0x0E, 0x13), 65),
        };

        // ---- 2) 평상시 층: 곡명만 ----
        _restTitle = new TextBlock
        {
            FontSize = TitleSizes[0],
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            // 한 줄에 안 들어가면 글꼴을 줄이고, 그래도 넘치면 두 줄까지 간다(FitTitle).
            // WPF TextBlock에는 MaxLines가 없어 MaxHeight로 줄 수를 제한한다.
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
        };
        _restArtist = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Subtle),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0),
        };
        // 글자가 놓인 자리에만 어두운 판을 깐다. 예전에는 창 아래쪽 전체에 그라데이션을 깔았는데
        // 표지의 아래 절반이 늘 어두워졌다 — 가릴 이유가 없는 데까지 가리는 셈이었다.
        var restStack = new StackPanel();
        restStack.Children.Add(_restTitle);
        restStack.Children.Add(_restArtist);

        _restPlate = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC4, 0x06, 0x09, 0x0D)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(PlatePadding, 6, PlatePadding, 7),
            Margin = new Thickness(PlateMargin),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = restStack,
        };

        // 좋아요는 <b>평상시에도</b> 보여야 한다 — 호버 층에만 있어 상태를 보려면 마우스를 올려야 했다.
        // 여기 있는 것은 표시 전용이다(누르는 것은 호버 층의 버튼).
        _restLove = new TextBlock
        {
            FontSize = 15,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9,
            },
        };

        // 의미는 **마우스를 올렸을 때만** 가운데에 보여 준다 — 평소에는 표지를 가리지 않는다.
        // 길면 잘라낸다(전문은 의미 창).
        _restMeaning = new TextBlock
        {
            FontSize = 12,
            LineHeight = 18,
            Foreground = new SolidColorBrush(Ink),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 8, 6, 8),
            Visibility = Visibility.Collapsed,
        };

        _rest = new Grid();
        _rest.Children.Add(_restPlate);
        _rest.Children.Add(_restLove);

        // ---- 3) 호버 층: 모든 컨트롤 ----
        _title = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 42,                 // 두 줄까지(WPF에는 MaxLines가 없다)
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _artist = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Subtle),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _source = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Dim),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };

        // 글리프는 Segoe Fluent Icons / MDL2의 공통 코드포인트를 쓴다(두 Windows 버전에서 같은 그림).
        _love = IconButton(() => ToggleLove(), HeartOutline, "mini.love.off");
        _meaning = IconButton(() => OnMeaningClick(), "", "mini.meaning");        // ReadingList
        _coverRetry = IconButton(() => RetryCover(), "", "mini.cover");           // Refresh
        _search = IconButton(() => _a.OpenSearch(), "", "mini.search");           // Search
        _openServer = IconButton(() => OpenOnServer(), "", "mini.openServer");    // OpenInNewWindow
        var songRow = Row(_love, _meaning, _coverRetry, _search, _openServer);

        // 우상단 창 버튼(30×30 두 개 + 여백)이 이 행을 함께 쓴다 — 그만큼 비워 두지 않으면
        // 긴 제목이 버튼 밑으로 깔려 글자가 가려진다.
        var top = new StackPanel { Margin = new Thickness(0, 0, WindowButtonsWidth, 0) };
        top.Children.Add(_title);
        top.Children.Add(_artist);
        top.Children.Add(_source);
        top.Children.Add(songRow);

        _prev = MediaButton(() => _a.OnPrevious(), "", "controls.previous");
        _playPause = MediaButton(() => _a.OnPlayPause(), "", "controls.playPause", primary: true);
        _next = MediaButton(() => _a.OnNext(), "", "controls.next");
        var playbackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        playbackRow.Children.Add(_prev);
        playbackRow.Children.Add(_playPause);
        playbackRow.Children.Add(_next);

        // 오프셋은 숫자가 곧 뜻이라 글리프로 대신할 수 없다 — 짧은 글자를 그대로 두고 납작하게만 만든다.
        _offsetMinus = TextButton(() => _a.AdjustOffset(-0.5), "mini.offset.minus");
        _offsetPlus = TextButton(() => _a.AdjustOffset(0.5), "mini.offset.plus");
        _offsetReset = TextButton(() => _a.AdjustOffset(null), "mini.offset.reset");
        _offsetLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Dim),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0),
        };
        var offsetRow = Row(_offsetMinus, _offsetPlus, _offsetReset);
        offsetRow.Children.Add(_offsetLabel);

        _openLyrics = IconButton(() => _a.OpenLyricsEditor(), "", "mini.openLyrics");   // Edit
        _wrong = IconButton(() => _a.MarkWrong(), "", "mini.wrong");                    // Warning
        _overlayToggle = IconButton(
            () => _a.SetOverlayVisible(!_a.IsOverlayVisible()), "", "mini.showOverlay"); // Caption
        _settingsButton = IconButton(() => _a.OpenSettings(), "", "mini.settings");           // Settings
        _exit = IconButton(() => { _closingToExit = true; _a.Exit(); }, "", "mini.exit"); // PowerButton
        var featureRow = Row(_openLyrics, _wrong, _overlayToggle, _settingsButton, _exit);

        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(playbackRow);
        bottom.Children.Add(offsetRow);
        bottom.Children.Add(featureRow);

        // 제목 표시줄이 없으니 최소화·닫기를 직접 둔다(호버할 때만 보인다).
        _minimize = IconButton(() => WindowState = WindowState.Minimized, "", "mini.minimize");
        _close = IconButton(() => Close(), "", "mini.close");
        var windowButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        windowButtons.Children.Add(_minimize);
        windowButtons.Children.Add(_close);

        var veilGrid = new Grid { Margin = new Thickness(12) };
        veilGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        veilGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        veilGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(top, 0);
        Grid.SetRow(_restMeaning, 1);   // 가운데 빈 칸 — 의미가 있으면 여기에 들어간다
        Grid.SetRow(bottom, 2);
        Grid.SetRow(windowButtons, 0);
        veilGrid.Children.Add(top);
        veilGrid.Children.Add(windowButtons);
        veilGrid.Children.Add(_restMeaning);
        veilGrid.Children.Add(bottom);

        _veil = new Grid
        {
            Background = Scrim(0.78, 0.93),
            Opacity = 0,
            IsHitTestVisible = false,   // 숨어 있을 때 버튼이 눌리면 안 된다
        };
        _veil.Children.Add(veilGrid);

        var root = new Grid();
        root.Children.Add(_coverFallback);
        root.Children.Add(_cover);
        root.Children.Add(_rest);
        root.Children.Add(_veil);
        Content = root;

        // 창을 키우면 제목이 더 큰 글꼴로 돌아갈 수 있다(줄이기만 하고 끝나면 안 된다).
        SizeChanged += (_, _) => FitTitle();

        root.MouseEnter += (_, _) => Reveal(true);
        root.MouseLeave += (_, _) => Reveal(false);

        // 제목 표시줄이 없으니 커버를 끌어 창을 옮긴다. 버튼 위에서 시작한 드래그는
        // 버튼이 먼저 먹으므로(Handled) 여기까지 오지 않는다.
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1) DragMove();
        };

        // 정사각 유지는 **크기 조절이 일어나는 동안**(WM_SIZING) 한다.
        // Width/Height를 나중에 대입하면 Windows는 왼쪽·위를 고정한 채 늘리므로,
        // 우상단을 잡고 끌었을 때 창이 따라 움직였다. 끌고 있는 변을 그대로 두려면
        // 사각형 자체를 그 자리에서 고쳐야 한다.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(KeepSquare);
        };
        // 키보드로도 닿아야 한다 — 탭으로 들어오면 열어 둔다.
        //
        // 단 **창을 활성화하는 것만으로는 열리면 안 된다.** WPF는 창이 활성화될 때 첫 포커스 가능
        // 요소(= 호버 층 안의 버튼)에 포커스를 주는데, 그러면 커버만 보여야 할 창이 컨트롤을 펼친
        // 채 뜬다. 그래서 **마지막 입력이 키보드였을 때만** 연다 — 탭으로 들어오는 길은 그대로 살고,
        // 트레이에서 창을 띄우는 경로(마지막 입력이 마우스)는 조용히 지나간다.
        _veil.GotKeyboardFocus += (_, _) =>
        {
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) Reveal(true);
        };
        _veil.LostKeyboardFocus += (_, _) => { if (!IsMouseOver) Reveal(false); };

        // 창이 다시 보일 때는 커버만 보이는 상태로 시작한다. 다만 마우스가 이미 그 자리에 있으면
        // 열어 두는 편이 맞다 — 지금까지는 커서를 한 번 움직여야 열렸다(MouseEnter가 안 온다).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            Reveal(IsMouseOver);
        };

        ApplyText();
        SyncOverlayVisible(_a.IsOverlayVisible());
        RefreshPlayback();
        RefreshOffset();
        RefreshLyricsFeatures();
        ApplyExtras(null);

        // 닫기(X): 옵션 켜짐=트레이로 숨김(Hide), 꺼짐(기본)=최소화(작업표시줄 상주). "종료"만 실제 닫힘.
        Closing += (_, e) =>
        {
            SavePlacement();
            if (_closingToExit) return;
            e.Cancel = true;
            if (_a.CloseToTray())
                Hide(); // 작업표시줄에서 사라지고 트레이로(트레이 더블클릭/제어판 열기로 복귀)
            else
                WindowState = WindowState.Minimized;
        };

        // 작업표시줄에서 복원(클릭) → 오버레이 되살리기.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _a.ReviveOverlay();
        };
        Activated += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _a.ReviveOverlay();
        };

        Loc.CultureChanged += ApplyText;
        Closed += (_, _) => Loc.CultureChanged -= ApplyText;
    }

    // ---- 정사각 유지 (WM_SIZING) ----

    private const int WmSizing = 0x0214;
    private const int WmExitSizeMove = 0x0232;
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4;
    private const int WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7;

    /// <summary>
    /// 끌고 있는 변을 고정한 채 사각형을 정사각으로 고친다 — 이게 "창이 움직이지 않는다"의 핵심이다.
    /// 좌우를 끌면 너비가, 위아래를 끌면 높이가 기준이 되고, 모서리는 너비를 기준으로 한다.
    /// </summary>
    private IntPtr KeepSquare(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 끌기를 놓는 순간 자리를 적어 둔다(이동·크기 조절 둘 다 이 메시지로 끝난다).
        if (msg == WmExitSizeMove) { SavePlacement(); return IntPtr.Zero; }
        if (msg != WmSizing) return IntPtr.Zero;

        var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<Rect32>(lParam);
        var edge = wParam.ToInt32();

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        // 기준 변: 좌우만 끌면 너비, 위아래만 끌면 높이, 모서리는 너비.
        var side = edge is WmszTop or WmszBottom ? height : width;

        // 끌지 않는 쪽을 움직여 정사각으로 만든다.
        if (edge is WmszLeft or WmszTopLeft or WmszBottomLeft) rect.Left = rect.Right - side;
        else rect.Right = rect.Left + side;

        if (edge is WmszTop or WmszTopLeft or WmszTopRight) rect.Top = rect.Bottom - side;
        else rect.Bottom = rect.Top + side;

        System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, fDeleteOld: false);
        handled = true;
        return (IntPtr)1;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    // ---- 호버 ----

    // ---- 곡 제목 맞추기 ----

    /// <summary>제목 글꼴 후보. 큰 것부터 재 보고 한 줄에 들어가는 첫 값을 쓴다.</summary>
    private static readonly double[] TitleSizes = [17, 15, 13];

    /// <summary>
    /// 제목이 판 안에 들어가도록 <b>글꼴을 줄이고, 그래도 넘치면 두 줄로</b> 간다.
    ///
    /// 예전에는 판 너비가 300으로 고정이고 줄바꿈이 없어, 창을 아무리 키워도 같은 자리에서
    /// 잘렸다. 이제 판은 창을 따라 넓어지고 글꼴은 필요한 만큼만 작아진다 —
    /// 무조건 작게 두면 짧은 제목까지 읽기 나빠진다.
    /// </summary>
    private void FitTitle()
    {
        var text = _restTitle.Text;
        if (string.IsNullOrEmpty(text)) return;

        // 판은 창 너비에서 바깥 여백(12+12)과 안쪽 패딩(10+10)을 뺀 만큼 쓸 수 있다.
        var available = Math.Max(80, ActualWidth - (PlateMargin * 2) - (PlatePadding * 2));
        _restPlate.MaxWidth = Math.Max(120, ActualWidth - (PlateMargin * 2));

        foreach (var size in TitleSizes)
        {
            Apply(size);
            if (MeasureWidth(text, size, FontWeights.SemiBold) <= available) return;
        }

        // 가장 작은 글꼴로도 한 줄에 안 들어간다 — 두 줄에 맡긴다(넘치면 말줄임).
        Apply(TitleSizes[^1]);

        void Apply(double size)
        {
            _restTitle.FontSize = size;
            _restTitle.LineHeight = Math.Round(size * 1.35);
            // LineHeight를 그대로 믿게 한다 — 기본 전략(MaxHeight)에서는 글꼴에 따라 줄 상자가
            // LineHeight보다 커질 수 있고, 그러면 두 줄이 상한을 넘겨 둘째 줄이 통째로 잘린다.
            _restTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            _restTitle.MaxHeight = (_restTitle.LineHeight * 2) + 2;
        }
    }

    /// <summary>그 글꼴로 한 줄에 그렸을 때의 너비.</summary>
    private double MeasureWidth(string text, double size, FontWeight weight)
    {
        var typeface = new Typeface(_restTitle.FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size,
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
    }

    // ---- 창 위치·크기 기억 ----

    /// <summary>창을 화면 끝에서 띄우는 여백. 모서리에 딱 붙으면 잡아 끌기가 어렵다.</summary>
    private const double EdgeMargin = 24;

    /// <summary>
    /// 저장해 둔 자리로 되돌린다. 없으면 <b>작업영역 우하단</b>에 놓는다 —
    /// 늘 띄워 두는 창이라 매번 화면 한가운데 나타나면 하던 일을 가린다.
    ///
    /// 저장된 좌표가 지금 화면 밖이면(모니터를 뗐다) 첫 실행 자리로 돌아간다.
    /// </summary>
    private void RestorePlacement()
    {
        var area = SystemParameters.WorkArea;

        if (_settings.PanelX is { } x && _settings.PanelY is { } y && OnSomeScreen(x, y))
        {
            Left = x;
            Top = y;
        }
        else
        {
            Left = area.Right - Width - EdgeMargin;
            Top = area.Bottom - Height - EdgeMargin;
        }
    }

    /// <summary>그 좌표에 창을 놓았을 때 어느 모니터엔가 실제로 걸치는가.</summary>
    private bool OnSomeScreen(double x, double y)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        // 모서리 한 점이 아니라 제목 판이 있는 만큼은 보여야 쓸모가 있다.
        return virtualScreen.IntersectsWith(new Rect(x, y, Width, Height))
            && x + Width > virtualScreen.Left + 40
            && y + Height > virtualScreen.Top + 40
            && x < virtualScreen.Right - 40
            && y < virtualScreen.Bottom - 40;
    }

    /// <summary>
    /// 지금 자리를 설정에 적는다. <b>화면 밖 보정을 먼저 한다</b> — 오버레이 쪽은 순서가 반대라
    /// 화면 밖으로 끌어 놓으면 보정 전 좌표가 저장되는 버그가 있었다(같은 실수를 되풀이하지 않는다).
    /// </summary>
    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal) return;   // 최소화 중의 좌표는 의미가 없다

        KeepOnScreen();
        _settings.PanelX = Left;
        _settings.PanelY = Top;
        _settings.PanelSize = Math.Clamp(ActualWidth, MinSize, MaxSize);
        _settings.Save();
    }

    /// <summary>창을 가상 화면 안으로 되돌린다(모니터 구성이 바뀐 뒤).</summary>
    private void KeepOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        Left = Math.Clamp(Left, left, Math.Max(left, right - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
    }

    /// <summary>컨트롤 층을 페이드로 켜고 끈다. 끌 때는 평상시 곡명 층을 되살린다.</summary>
    private void Reveal(bool on)
    {
        _veil.IsHitTestVisible = on;
        Fade(_veil, on ? 1 : 0);
        Fade(_rest, on ? 0 : 1);
    }

    private static void Fade(UIElement target, double to) =>
        target.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

    /// <summary>커버 위 글자가 읽히도록 아래쪽을 어둡게 까는 그라데이션.</summary>
    private static LinearGradientBrush Scrim(double top, double bottom) => new(
        new GradientStopCollection
        {
            new(Color.FromArgb((byte)(top * 255), 0x06, 0x09, 0x0D), 0),
            new(Color.FromArgb((byte)(bottom * 255), 0x06, 0x09, 0x0D), 1),
        },
        new Point(0.5, 0), new Point(0.5, 1));

    // ---- 곡에 딸린 것들 ----

    /// <summary>
    /// 곡이 바뀌면 표지·좋아요·의미를 다시 받아 온다.
    ///
    /// 표지는 두 곳에서 오는데 <b>서로 기다리지 않는다</b> — 재생 앱이 준 표지(SMTC)는 네트워크가
    /// 없어 거의 즉시 오고, 서버가 이미 가진 커버도 한 번의 왕복이면 온다. 둘을 나란히 띄워
    /// 먼저 도착한 쪽을 건다.
    ///
    /// 다만 <b>서버 커버가 오면 그쪽이 이긴다</b> — 재생 앱 표지는 300px에 로고 띠·여백이 붙어
    /// 오는 반면 서버 것은 600px 정사각이라 훨씬 깨끗하다.
    ///
    /// 그 사이 곡이 또 바뀌면 늦게 온 응답은 버린다.
    /// </summary>
    private void FetchExtras()
    {
        var epoch = ++_extrasEpoch;
        _hasServerCover = false;
        _extrasFailed = false;
        _cover.Source = null;      // 곡이 바뀌었다 — 앞 곡 표지를 남겨 두지 않는다
        ApplyMeaning(null);
        ApplyExtras(null);

        FetchThumbnail(epoch);
        FetchServerExtras(epoch);
        FetchMeaning(epoch);
        ChaseCover(epoch);
    }

    /// <summary>
    /// 커버가 <b>나중에 생기는</b> 곡을 따라잡는다.
    ///
    /// 처음 트는 곡은 서버에 가사 행이 아직 없어 <c>GET /v1/song</c>이 404다 — 그 재생 내내
    /// 커버가 비어 있었다. 가사를 올리고 나면 생기지만, 그때는 화면 상태가 더 바뀌지 않아
    /// 다시 물어볼 계기가 없다. 그래서 몇 번만 되물어 본다.
    ///
    /// 곡이 바뀌면(<paramref name="epoch"/>) 멈추고, 커버가 걸리면 더 묻지 않는다.
    /// </summary>
    private async void ChaseCover(int epoch)
    {
        if (_a.GetExtras is not { } get) return;

        foreach (var wait in CoverChaseDelays)
        {
            await Task.Delay(wait);
            if (epoch != _extrasEpoch || _hasServerCover) return;

            var result = await get();
            if (epoch != _extrasEpoch) return;

            // 아직 서버에 없는 곡(404)이면 다음 차례에 다시 — 그 사이 이 기기가 올린다.
            // 못 닿은 경우도 같이 넘기되, 화면 표시는 ApplyResult가 맞춰 준다.
            if (result.Extras is null) { ApplyResult(result); continue; }

            ApplyResult(result);
            if (_hasServerCover) return;
        }
    }

    /// <summary>커버를 되묻는 간격. 가사 검색·번역·업로드가 끝나는 시간을 넉넉히 덮는다.</summary>
    private static readonly TimeSpan[] CoverChaseDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(40)];

    private async void FetchThumbnail(int epoch)
    {
        if (_a.GetThumbnail is not { } thumb) return;

        var bytes = await thumb();
        if (epoch != _extrasEpoch || _hasServerCover) return;
        // 서버 커버가 없을 때만 쓴다 — 재생 앱 표지는 작고(300px) 로고 띠·여백이 붙어 온다.
        ShowThumbnail(bytes);
    }

    private async void FetchServerExtras(int epoch)
    {
        if (_a.GetExtras is not { } get) return;

        var result = await get();
        if (epoch != _extrasEpoch) return;
        ApplyResult(result);
    }

    /// <summary>
    /// 서버 응답을 화면에 반영한다.
    ///
    /// <b>"그 곡을 모른다"(404)는 오류가 아니다.</b> 처음 트는 곡은 아직 아무도 올리지 않았을 뿐이고
    /// 몇 초 뒤 이 기기가 올린다 — 그걸 경고로 그리면 <b>새 곡마다 빨간 표시가 뜬다</b>.
    /// 실제로 못 닿았을 때만 알린다.
    /// </summary>
    private void ApplyResult(SongExtrasResult result)
    {
        // 뒤늦게 성공하면 앞서 띄운 경고를 반드시 내린다 — 안 내리면 곡이 끝날 때까지 남는다.
        _extrasFailed = result.Reach == ExtrasReach.Failed;

        ApplyExtras(result.Extras);
        if (_extrasFailed) _source.Text = Loc.T("mini.extras.failed");
    }

    private async void FetchMeaning(int epoch)
    {
        if (_a.GetMeaning is not { } getMeaning) return;

        var meaning = await getMeaning();
        if (epoch != _extrasEpoch) return;
        ApplyMeaning(meaning);
    }

    /// <summary>의미 본문을 평상시 층 가운데에 건다(없으면 감춘다).</summary>
    private void ApplyMeaning(SongMeaningView? meaning)
    {
        _hasMeaning = meaning is not null;
        _restMeaning.Text = meaning?.Summary ?? "";
        _restMeaning.Visibility = _hasMeaning ? Visibility.Visible : Visibility.Collapsed;
        ApplyMeaningLabel();
    }

    /// <summary>
    /// 의미 버튼을 누른다. 있으면 창을 열고, <b>없으면 그 자리에서 만든다</b> —
    /// 궁금해서 누른 것이니 "없습니다"로 끝내고 다시 누르게 할 이유가 없다.
    /// </summary>
    private async void OnMeaningClick()
    {
        if (_hasMeaning || _a.MakeMeaning is not { } make)
        {
            _a.OpenMeaning?.Invoke();
            return;
        }

        var epoch = _extrasEpoch;
        _meaning.IsEnabled = false;
        _meaning.ToolTip = Loc.T("mini.meaning.making");

        var result = await make();
        if (epoch != _extrasEpoch) return;

        _meaning.IsEnabled = true;
        // 만들었으면 본문을 바로 가운데에 건다 — 누른 사람이 원한 것은 글이지 버튼 상태가 아니다.
        ApplyMeaning(result.Meaning);
        if (!_hasMeaning) _source.Text = MeaningFailureText(result.Status);
    }

    /// <summary>만들지 못한 이유를 상태 줄에 적는다 — 이유마다 다시 누를 만한지가 갈린다.</summary>
    private static string MeaningFailureText(MeaningRequestStatus status) => status switch
    {
        MeaningRequestStatus.NoSource => Loc.T("mini.meaning.noSource"),
        MeaningRequestStatus.Insufficient => Loc.T("mini.meaning.insufficient"),
        MeaningRequestStatus.Retry => Loc.T("mini.meaning.retry"),
        MeaningRequestStatus.Unavailable => Loc.T("mini.meaning.unavailable"),
        _ => Loc.T("mini.meaning.failed"),
    };

    private void ApplyMeaningLabel() =>
        _meaning.ToolTip = Loc.T(_hasMeaning ? "mini.meaning" : "mini.meaning.find");

    private async void ToggleLove()
    {
        if (_a.SetLoved is not { } set || _extras is not { LoveConnected: true } now) return;

        _love.IsEnabled = false;
        var epoch = _extrasEpoch;
        // 모르는 상태(조회 실패)면 끄는 쪽으로 보내지 않는다 — 이미 켜 둔 것을 꺼 버릴 수 있다.
        // 켜는 요청은 이미 켜져 있어도 해가 없고, 응답으로 실제 상태를 받아 온다.
        var updated = await set(!now.LoveKnown || !now.Loved);
        if (epoch != _extrasEpoch) return;

        _love.IsEnabled = true;
        if (updated is not null) ApplyExtras(updated);
    }

    /// <summary>
    /// 이 곡의 가사 서버 화면 주소. 키는 <b>서버가 준 것만</b> 쓴다 — 정규화 규칙이 서버 몫이라
    /// 앱이 제목·아티스트로 만들면 같은 곡이라도 다른 키가 나온다. 키가 없거나(구버전 서버)
    /// 서버 주소를 안 넣었으면 null이고, 그러면 버튼 자체를 감춘다.
    /// </summary>
    private string? ServerUrl() => _extras?.AdminUrl(_a.ServerEndpoint?.Invoke());

    private void OpenOnServer()
    {
        if (ServerUrl() is not { } url) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write($"[mini] 가사 서버 열기 실패: {e.Message}");
        }
    }

    private async void RetryCover()
    {
        if (_a.RefreshCover is not { } refresh) return;

        _coverRetry.IsEnabled = false;
        var epoch = _extrasEpoch;
        var updated = await refresh();
        if (epoch != _extrasEpoch) return;

        _coverRetry.IsEnabled = true;
        if (updated is not null) ApplyExtras(updated);
    }

    private void ApplyExtras(SongExtras? extras)
    {
        _extras = extras;
        LoadCover(extras?.CoverUrl);

        // 연결돼 있지 않으면 좋아요 자리를 아예 비운다 — 눌러도 안 되는 버튼은 헷갈리게 한다.
        var connected = extras is { LoveConnected: true };
        _love.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (connected)
        {
            _love.Content = LoveGlyph(extras);
            _love.ToolTip = LoveTip(extras);
            _love.Foreground = new SolidColorBrush(LoveColor(extras));
        }

        ApplyLoveBadge();

        _coverRetry.Visibility = _a.RefreshCover is null ? Visibility.Collapsed : Visibility.Visible;
        _meaning.Visibility = _a.OpenMeaning is null ? Visibility.Collapsed : Visibility.Visible;
        _openServer.Visibility = ServerUrl() is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 평상시 층 우상단의 좋아요 표시. <b>세 가지를 구별한다</b> —
    /// 켬(♥) · 끔(♡) · <b>모름(!)</b>. 모르는 것을 꺼진 하트로 그리면 사람이 눌러서 이미 켜 둔 것을
    /// 끄게 되므로, 확인하지 못한 상태는 반드시 다르게 보여야 한다.
    ///
    /// 서버에 물어보지도 못했으면(⚠) 좋아요가 아니라 <b>연결 문제</b>다 — 그것도 조용히 두지 않는다.
    /// </summary>
    private void ApplyLoveBadge()
    {
        if (_extrasFailed)
        {
            _restLove.Text = "⚠";
            _restLove.Foreground = new SolidColorBrush(Warn);
            _restLove.ToolTip = Loc.T("mini.extras.failed");
            _restLove.Visibility = Visibility.Visible;
            return;
        }

        if (_extras is not { LoveConnected: true } extras)
        {
            _restLove.Visibility = Visibility.Collapsed;
            return;
        }

        _restLove.Text = extras.LoveKnown ? (extras.Loved ? "♥" : "♡") : "!";
        _restLove.Foreground = new SolidColorBrush(
            extras.LoveKnown ? (extras.Loved ? Heart : Ink) : Warn);
        _restLove.ToolTip = LoveTip(extras);
        _restLove.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 아이콘 글꼴(Segoe Fluent Icons / MDL2)의 하트. <b>♡·♥(U+2661·U+2665)를 쓰면 안 된다</b> —
    /// 그 글꼴은 사용자 정의 영역 전용이라 저 문자가 없고, 폴백이 실패하면 두부(□)로 떨어진다.
    /// </summary>
    private const string HeartOutline = "";   // Heart
    private const string HeartFilled = "";    // HeartFill

    /// <summary>
    /// 좋아요 버튼에 그릴 글자. <b>모르는 상태는 색으로 가른다</b>(<see cref="Warn"/>) —
    /// 아이콘 글꼴에는 "물음표 붙은 하트"가 없고, 글자를 덧붙이면 원형 버튼 밖으로 삐져나온다.
    /// </summary>
    private static string LoveGlyph(SongExtras? extras) =>
        extras is { LoveKnown: true, Loved: true } ? HeartFilled : HeartOutline;

    /// <summary>버튼 글자색 — 켬은 분홍, 모름은 주황, 끔은 보통.</summary>
    private static Color LoveColor(SongExtras? extras) =>
        extras is not { LoveKnown: true } ? Warn : extras.Loved ? Heart : Ink;

    private static string LoveTip(SongExtras? extras) => Loc.T(
        extras is not { LoveKnown: true } ? "mini.love.unknown"
        : extras.Loved ? "mini.love.on" : "mini.love.off");

    /// <summary>재생 앱이 준 표지 바이트를 건다. 성공하면 true(서버 커버를 부르지 않아도 된다).</summary>
    private bool ShowThumbnail(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return false;

        var bitmap = Decode(new MemoryStream(bytes, writable: false));
        if (bitmap is null) return false;

        _cover.Source = SquareTop(bitmap);
        return true;
    }

    /// <summary>
    /// 표지 아래에 붙어 오는 <b>로고 띠를 찾아 잘라낸다.</b>
    ///
    /// Spotify는 표지를 <b>정사각 그대로</b>(실측 300×300) 주면서 그 안의 아래쪽에 로고 띠를
    /// 그려 넣는다 — 그래서 "세로로 길면 자른다" 같은 규칙으로는 잡히지 않는다. 대신 띠의
    /// 성질을 쓴다: <b>로고는 가운데에 있고 좌우 끝은 띠 배경색 그대로다.</b> 아래에서 위로
    /// 올라가며 양쪽 끝 픽셀이 모서리 색과 같은 행을 세면 띠 높이가 나온다.
    ///
    /// 앨범 아트의 아래쪽이 우연히 단색일 수 있으므로 자르는 양을 <b>높이의 25%까지</b>로
    /// 제한한다(그마저도 단색이라 잘려도 그림이 달라지지 않는다). 찾은 값은 로그에 남긴다.
    /// </summary>
    private static BitmapSource SquareTop(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0) return source;

        var art = ArtRect(source);
        Log.Write($"[cover] 재생 앱 표지 {width}x{height} → 아트 {art.Width}x{art.Height}"
                  + $" (왼{art.X} 위{art.Y} 오른{width - art.X - art.Width} 아래{height - art.Y - art.Height})");

        if (art.Width <= 0 || art.Height <= 0
            || (art.Width == width && art.Height == height)) return source;

        try
        {
            var cropped = new CroppedBitmap(source, art);
            cropped.Freeze();
            return cropped;
        }
        catch (Exception)
        {
            return source; // 자르기 실패는 원본 그대로(로고가 보이는 편이 빈 창보다 낫다)
        }
    }

    /// <summary>
    /// 단색 테두리를 걷어 낸 <b>실제 그림 영역</b>.
    ///
    /// Spotify 표지는 정사각 판(300×300) 안에 아트를 넣고 남는 자리를 검게 채운다 —
    /// 아래에는 로고 띠, 좌우에는 여백이 생긴다. 그래서 한쪽만 보면 안 되고 네 변을 모두 본다.
    ///
    /// 좌·우·위 테두리는 <b>줄 전체</b>가 배경색이어야 인정한다. 아래는 다르다 —
    /// 로고가 가운데에 그려져 있으므로 <b>양쪽 끝 픽셀</b>만 본다.
    ///
    /// 한 변에서 최대 30%까지만 걷어 낸다(단색 배경의 앨범 아트를 통째로 깎지 않도록).
    /// </summary>
    private static Int32Rect ArtRect(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var whole = new Int32Rect(0, 0, width, height);
        if (width < 8 || height < 8) return whole;

        try
        {
            var px = new BitmapImageConverter(source);
            var bg = px.At(0, height - 1);   // 아래쪽 모서리 — 띠·여백이 있으면 그 색이다

            var maxX = width * 3 / 10;
            var maxY = height * 3 / 10;

            bool Column(int x)
            {
                for (var y = 0; y < height; y++) if (!Near(px.At(x, y), bg)) return false;
                return true;
            }
            bool Row(int y)
            {
                for (var x = 0; x < width; x++) if (!Near(px.At(x, y), bg)) return false;
                return true;
            }

            var left = 0;
            while (left < maxX && Column(left)) left++;

            var right = 0;
            while (right < maxX && Column(width - 1 - right)) right++;

            var top = 0;
            while (top < maxY && Row(top)) top++;

            // 아래 띠: 로고가 끼어 있어 줄 전체가 같을 수 없다 — 양 끝만 본다.
            var bottom = 0;
            while (bottom < maxY
                   && Near(px.At(0, height - 1 - bottom), bg)
                   && Near(px.At(width - 1, height - 1 - bottom), bg)) bottom++;

            // 한두 줄 같은 것은 흔하다 — 테두리라고 부를 두께가 아니면 무시한다.
            if (left < 3) left = 0;
            if (right < 3) right = 0;
            if (top < 3) top = 0;
            if (bottom < 3) bottom = 0;

            var rect = new Int32Rect(left, top, width - left - right, height - top - bottom);
            return rect.Width > width / 2 && rect.Height > height / 2 ? rect : whole;
        }
        catch (Exception)
        {
            return whole;
        }
    }

    /// <summary>
    /// 두 색이 <b>사실상 같은가</b>(채널별 차이 합). 넉넉하게 잡으면 앨범 아트의 어두운
    /// 가장자리까지 테두리로 오인해 그림을 깎는다 — 실측에서 오른쪽·아래가 조금 잘렸다.
    /// </summary>
    private static bool Near((byte B, byte G, byte R) a, (byte B, byte G, byte R) b) =>
        Math.Abs(a.B - b.B) + Math.Abs(a.G - b.G) + Math.Abs(a.R - b.R) <= 6;

    /// <summary>픽셀을 좌표로 읽기 위한 최소 래퍼(한 번만 복사한다).</summary>
    private sealed class BitmapImageConverter
    {
        private readonly byte[] _pixels;
        private readonly int _stride;

        public BitmapImageConverter(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            _stride = converted.PixelWidth * 4;
            _pixels = new byte[_stride * converted.PixelHeight];
            converted.CopyPixels(_pixels, _stride, 0);
        }

        public (byte B, byte G, byte R) At(int x, int y)
        {
            var i = y * _stride + x * 4;
            return (_pixels[i], _pixels[i + 1], _pixels[i + 2]);
        }
    }

    /// <summary>
    /// 서버가 알려 준 커버 주소를 건다(표지를 못 받았을 때의 폴백).
    ///
    /// <b>바이트를 직접 받아 디코드한다.</b> <see cref="BitmapImage.UriSource"/>에 http 주소를
    /// 그대로 주면 WPF가 제 방식으로 내려받는데, 실패가 조용히 묻혀 "왜 커버가 안 뜨지"로
    /// 끝나고 UI 스레드를 붙잡을 수도 있다. 실패는 여기서 확실히 잡아 대체 배경으로 돌아간다.
    /// </summary>
    private async void LoadCover(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var epoch = _extrasEpoch;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (epoch != _extrasEpoch) return;

            var bitmap = Decode(new MemoryStream(bytes, writable: false));
            if (bitmap is null) return;

            // 서버 커버는 자르지 않는다 — iTunes·Deezer는 정사각 원본(600px)을 그대로 준다.
            _cover.Source = bitmap;
            _hasServerCover = true;
        }
        catch (Exception)
        {
            // 재생 앱 표지나 대체 배경이 남아 있다 — 창이 깨지지 않는다.
        }
    }

    /// <summary>스트림 → 즉시 디코드한 비트맵. 깨진 데이터면 null.</summary>
    private static BitmapImage? Decode(Stream stream)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 스트림을 닫아도 되게 즉시 읽는다
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>커버 내려받기 전용. 표지를 못 받은 곡에만 쓰이므로 짧게 끊는다.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // ---- 버튼 ----

    /// <summary>
    /// 커버 위에 얹는 납작한 버튼. WPF 기본 버튼 크롬은 밝은 배경을 전제로 해서
    /// 사진 위에 놓으면 튄다 — 템플릿을 직접 준다.
    /// </summary>
    private static Button Chip(Action onClick, bool primary = false, double size = 0)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(size > 0 ? size / 2 : 7));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush))
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var button = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            Background = new SolidColorBrush(primary
                ? Accent
                : Color.FromArgb(0xB8, 0x14, 0x19, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(primary ? Color.FromRgb(0x06, 0x12, 0x1D) : Ink),
            FontSize = size > 0 ? 14 : 11,
            FontWeight = primary ? FontWeights.Bold : FontWeights.Normal,
            Padding = size > 0 ? new Thickness(0) : new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 5, 5),
            Cursor = Cursors.Hand,
        };
        if (size > 0)
        {
            button.Width = size;
            button.Height = size;
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// 아이콘 버튼 — <b>네모를 없앤다</b>. 예전에는 모든 버튼이 <see cref="Chip"/>이라 반투명 판과
    /// 흰 테두리가 커버 위를 덮었다. 이제 평소에는 글리프만 떠 있고, <b>마우스를 올렸을 때만</b>
    /// 은은한 원이 생긴다(오버레이의 재생 컨트롤이 쓰던 방식과 같다).
    ///
    /// 글꼴을 <b>명시</b>하는 것이 중요하다. 지금까지는 FontFamily를 주지 않아 ⏮ ▶ 같은 문자가
    /// 폰트 폴백에 따라 네모(두부)로 떨어질 수 있었다 — Segoe Fluent Icons(Win11) / Segoe MDL2
    /// Assets(Win10)를 차례로 지정해 두 버전 모두에서 같은 그림이 나오게 한다.
    ///
    /// 못 쓰는 버튼은 <b>흐려진다</b>. Chip 템플릿에는 IsEnabled 트리거가 없어 눌리지만 않을 뿐
    /// 멀쩡해 보였다.
    /// </summary>
    private static Button IconButton(
        Action onClick, string glyph, string tooltipKey, double size = 30, bool filled = false)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        if (filled)
            // 채운 원(재생·일시정지) — 배경을 버튼에서 받아 그린다.
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        else
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        if (!filled)
            template.Triggers.Add(new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters = { new Setter(Border.BackgroundProperty, HoverFill, "bg") },
            });
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(UIElement.OpacityProperty, 0.35, "bg") },
        });

        var button = new Button
        {
            Template = template,
            Content = glyph,
            FontFamily = IconFont,
            FontSize = size >= 34 ? 15 : 13,
            Foreground = new SolidColorBrush(Ink),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = size,
            Height = size,
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T(tooltipKey),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>글리프로 대신할 수 없는 짧은 글자 버튼(오프셋 ±0.5s) — 생김새는 아이콘 버튼과 같다.</summary>
    private static Button TextButton(Action onClick, string tooltipKey)
    {
        var button = IconButton(onClick, "", tooltipKey, size: 30);
        button.FontFamily = SystemFonts.MessageFontFamily;
        button.FontSize = 11;
        button.Width = double.NaN;                        // 글자 길이에 맞춘다
        button.Padding = new Thickness(8, 0, 8, 0);
        return button;
    }

    /// <summary>Windows 11의 Segoe Fluent Icons, 없으면 Windows 10의 Segoe MDL2 Assets.</summary>
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Brush HoverFill = Freeze(
        new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 재생 버튼. 재생/일시정지만 채운 원으로 띄워 어디를 누를지 한눈에 보이게 한다 —
    /// 나머지는 글리프만.
    /// </summary>
    private static Button MediaButton(Action onClick, string glyph, string tooltipKey, bool primary = false)
    {
        var button = IconButton(onClick, glyph, tooltipKey, size: primary ? 40 : 34, filled: primary);
        if (!primary) return button;

        button.Background = new SolidColorBrush(Accent);
        button.Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0x12, 0x1D));
        return button;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    // ---- 갱신(트레이와 동기화) ----

    /// <summary>가사 소스/상태 한 줄을 갱신한다(코디네이터 CurrentStatus 현지화 문자열).</summary>
    public void SetStatus(string text) => _source.Text = text;

    /// <summary>곡 제목/아티스트를 갱신한다. 곡 없으면 "재생 없음".</summary>
    public void SetTrack(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            _title.Text = _restTitle.Text = Loc.T("mini.noTrack");
            FitTitle();
            _artist.Text = _restArtist.Text = "";
            _artist.Visibility = _restArtist.Visibility = Visibility.Collapsed;
            _extrasEpoch++;          // 진행 중인 조회 결과를 버린다
            _fetchedKey = null;
            _extrasFailed = false;
            ApplyExtras(null);
            return;
        }

        _title.Text = _restTitle.Text = title;
        _artist.Text = _restArtist.Text = artist ?? "";
        FitTitle();
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        _artist.Visibility = _restArtist.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        // 이 메서드는 가사 상태가 바뀔 때마다(곡당 여러 번) 불린다. 예전에는 그때마다 다시 받느라
        // 커버가 지워졌다 다시 걸려 깜빡였고 GET도 곡당 여러 번 나갔다 — 곡이 정말 바뀐 때만 받는다.
        var key = $"{title}|{artist}";
        if (key == _fetchedKey) return;
        _fetchedKey = key;

        FetchExtras();
    }

    /// <summary>오버레이 표시 상태에 맞춰 토글 버튼 라벨을 갱신한다(트레이와 동기화).</summary>
    public void SyncOverlayVisible(bool visible) =>
        _overlayToggle.ToolTip = Loc.T(visible ? "mini.hideOverlay" : "mini.showOverlay");

    /// <summary>재생 상태·컨트롤 가용성에 맞춰 재생 컨트롤 행을 갱신한다.</summary>
    public void RefreshPlayback()
    {
        var c = _a.GetControls();
        var playing = _a.IsPlaying();
        // 글리프는 고정(아이콘 글꼴), 설명만 언어를 따른다.
        _playPause.Content = playing ? "" : "";   // Pause / Play
        _prev.ToolTip = Loc.T("controls.previous");
        _next.ToolTip = Loc.T("controls.next");
        _playPause.ToolTip = Loc.T("controls.playPause");
        _prev.IsEnabled = c.CanPrevious;
        _playPause.IsEnabled = c.CanPlayPause;
        _next.IsEnabled = c.CanNext;
    }

    /// <summary>현재 오프셋 라벨을 갱신한다(트레이 라벨과 동일 포맷).</summary>
    public void RefreshOffset() =>
        _offsetLabel.Text = Loc.T("mini.offset.label", ("value", _a.GetOffset().ToString("+0.0;-0.0;0")));

    /// <summary>가사 유무에 따라 열기·틀린가사 버튼을 활성/비활성(트레이 editItem/wrongItem과 동기화).</summary>
    public void RefreshLyricsFeatures()
    {
        var hasLyrics = _a.HasLyrics();
        _openLyrics.IsEnabled = hasLyrics;
        _wrong.IsEnabled = hasLyrics;
    }

    private void ApplyText()
    {
        Title = Loc.T("mini.title");
        SyncOverlayVisible(_a.IsOverlayVisible());
        // 아이콘 버튼은 글자가 아니라 툴팁이 설명을 담는다 — 언어를 바꾸면 툴팁이 따라간다.
        _settingsButton.ToolTip = Loc.T("mini.settings");
        _exit.ToolTip = Loc.T("mini.exit");
        _search.ToolTip = Loc.T("mini.search");
        _openLyrics.ToolTip = Loc.T("mini.openLyrics");
        _wrong.ToolTip = Loc.T("mini.wrong");
        _coverRetry.ToolTip = Loc.T("mini.cover");
        _openServer.ToolTip = Loc.T("mini.openServer");
        _minimize.ToolTip = Loc.T("mini.minimize");
        _close.ToolTip = Loc.T("mini.close");
        _offsetMinus.Content = Loc.T("mini.offset.minus");
        _offsetPlus.Content = Loc.T("mini.offset.plus");
        _offsetReset.Content = Loc.T("mini.offset.reset");
        ApplyMeaningLabel();
        _love.Content = LoveGlyph(_extras);
        _love.Foreground = new SolidColorBrush(LoveColor(_extras));
        _love.ToolTip = LoveTip(_extras);
        ApplyLoveBadge();
        RefreshOffset();
        RefreshPlayback();
    }

    /// <summary>트레이(더블클릭/제어판 열기)에서 미니창을 다시 앞으로 가져온다.</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _a.ReviveOverlay();
    }

    /// <summary>종료 경로(트레이 종료 등)에서 실제 닫힘을 허용하고 창을 닫는다.</summary>
    public void CloseForExit()
    {
        _closingToExit = true;
        Close();
    }
}
