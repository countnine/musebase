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
    Func<Task<SongExtras?>>? GetExtras = null,
    Func<bool, Task<SongExtras?>>? SetLoved = null,
    Func<Task<SongExtras?>>? RefreshCover = null,
    // 재생 앱(SMTC)이 준 앨범 표지 — 서버 커버보다 우선한다
    Func<Task<byte[]?>>? GetThumbnail = null,
    // 의미: 있으면 창을 열고, 없으면 그 자리에서 만든다
    Func<Task<SongMeaningView?>>? GetMeaning = null,
    Func<Task<MeaningRequestResult>>? MakeMeaning = null);

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
    private static readonly Color Accent = Color.FromRgb(0x7C, 0xC4, 0xFF);
    private static readonly Color Heart = Color.FromRgb(0xFF, 0x8F, 0xB1);

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
    private readonly Button _settings;
    private readonly Button _exit;
    private readonly Button _love;
    private readonly Button _meaning;
    private readonly Button _coverRetry;
    private readonly Button _minimize;
    private readonly Button _close;

    private bool _closingToExit;   // "종료" 경로에서만 실제 닫힘 허용
    private SongExtras? _extras;
    private int _extrasEpoch;      // 곡이 바뀌면 늦게 도착한 응답을 버린다
    private bool _hasThumbnail;    // 재생 앱이 준 표지를 이미 걸었는가(서버 커버로 덮지 않는다)
    private bool _hasMeaning;      // 의미가 이미 있는가 — 버튼 글자가 이것으로 갈린다

    public MiniWindow(System.Drawing.Icon? appIcon, MiniWindowActions actions)
    {
        _a = actions;

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
        Width = Height = ArtSize;
        MinWidth = MinHeight = MinSize;
        MaxWidth = MaxHeight = MaxSize;
        Topmost = true;             // 가사창과 같이 쓰는 창이라 뒤로 숨으면 쓸모가 없다
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _restArtist = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Dim),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var restStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(14, 12, 14, 12),
        };
        restStack.Children.Add(_restTitle);
        restStack.Children.Add(_restArtist);

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
        // 위는 거의 투명하게 두고(커버가 보여야 한다) 아래로 갈수록 어둡게 — 글자가 읽히도록.
        _rest.Children.Add(new Border { Background = Scrim(0.0, 0.62) });
        _rest.Children.Add(restStack);

        // ---- 3) 호버 층: 모든 컨트롤 ----
        _title = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _artist = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Dim),
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

        _love = Chip(() => ToggleLove());
        _meaning = Chip(() => OnMeaningClick());
        _coverRetry = Chip(() => RetryCover());
        _search = Chip(() => _a.OpenSearch());
        var songRow = Row(_love, _meaning, _coverRetry, _search);

        var top = new StackPanel();
        top.Children.Add(_title);
        top.Children.Add(_artist);
        top.Children.Add(_source);
        top.Children.Add(songRow);

        _prev = MediaButton(() => _a.OnPrevious());
        _playPause = MediaButton(() => _a.OnPlayPause(), primary: true);
        _next = MediaButton(() => _a.OnNext());
        var playbackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        playbackRow.Children.Add(_prev);
        playbackRow.Children.Add(_playPause);
        playbackRow.Children.Add(_next);

        _offsetMinus = Chip(() => _a.AdjustOffset(-0.5));
        _offsetPlus = Chip(() => _a.AdjustOffset(0.5));
        _offsetReset = Chip(() => _a.AdjustOffset(null));
        _offsetLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Dim),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0),
        };
        var offsetRow = Row(_offsetMinus, _offsetPlus, _offsetReset);
        offsetRow.Children.Add(_offsetLabel);

        _openLyrics = Chip(() => _a.OpenLyricsEditor());
        _wrong = Chip(() => _a.MarkWrong());
        _overlayToggle = Chip(() => _a.SetOverlayVisible(!_a.IsOverlayVisible()));
        _settings = Chip(() => _a.OpenSettings());
        _exit = Chip(() => { _closingToExit = true; _a.Exit(); });
        var featureRow = Row(_openLyrics, _wrong, _overlayToggle, _settings, _exit);

        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(playbackRow);
        bottom.Children.Add(offsetRow);
        bottom.Children.Add(featureRow);

        // 제목 표시줄이 없으니 최소화·닫기를 직접 둔다(호버할 때만 보인다).
        _minimize = Chip(() => WindowState = WindowState.Minimized);
        _close = Chip(() => Close());
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
        _veil.GotKeyboardFocus += (_, _) => Reveal(true);
        _veil.LostKeyboardFocus += (_, _) => { if (!IsMouseOver) Reveal(false); };

        ApplyText();
        SyncOverlayVisible(_a.IsOverlayVisible());
        RefreshPlayback();
        RefreshOffset();
        RefreshLyricsFeatures();
        ApplyExtras(null);

        // 닫기(X): 옵션 켜짐=트레이로 숨김(Hide), 꺼짐(기본)=최소화(작업표시줄 상주). "종료"만 실제 닫힘.
        Closing += (_, e) =>
        {
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
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4;
    private const int WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7;

    /// <summary>
    /// 끌고 있는 변을 고정한 채 사각형을 정사각으로 고친다 — 이게 "창이 움직이지 않는다"의 핵심이다.
    /// 좌우를 끌면 너비가, 위아래를 끌면 높이가 기준이 되고, 모서리는 너비를 기준으로 한다.
    /// </summary>
    private IntPtr KeepSquare(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
    /// 먼저 도착한 쪽을 건다. 다만 <b>SMTC 표지가 오면 그쪽이 이긴다</b> — 지금 나오는 음원과
    /// 정확히 같은 앨범이라 리마스터·싱글 버전까지 맞는다(검색은 엉뚱한 앨범을 집을 수 있다).
    ///
    /// 그 사이 곡이 또 바뀌면 늦게 온 응답은 버린다.
    /// </summary>
    private void FetchExtras()
    {
        var epoch = ++_extrasEpoch;
        _hasThumbnail = false;
        ApplyMeaning(null);
        ApplyExtras(null);

        FetchThumbnail(epoch);
        FetchServerExtras(epoch);
        FetchMeaning(epoch);
    }

    private async void FetchThumbnail(int epoch)
    {
        if (_a.GetThumbnail is not { } thumb) return;

        var bytes = await thumb();
        if (epoch != _extrasEpoch) return;
        // 서버 커버가 먼저 걸려 있어도 덮는다 — 이쪽이 정확하다.
        _hasThumbnail = ShowThumbnail(bytes);
    }

    private async void FetchServerExtras(int epoch)
    {
        if (_a.GetExtras is not { } get) return;

        var extras = await get();
        if (epoch != _extrasEpoch) return;
        ApplyExtras(extras);
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
        _meaning.Content = Loc.T("mini.meaning.making");

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
        _meaning.Content = Loc.T(_hasMeaning ? "mini.meaning" : "mini.meaning.find");

    private async void ToggleLove()
    {
        if (_a.SetLoved is not { } set || _extras is not { LoveConnected: true } now) return;

        _love.IsEnabled = false;
        var epoch = _extrasEpoch;
        var updated = await set(!now.ShowLoved);
        if (epoch != _extrasEpoch) return;

        _love.IsEnabled = true;
        if (updated is not null) ApplyExtras(updated);
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
        // 재생 앱이 준 표지가 이미 걸려 있으면 서버 커버로 덮지 않는다 — 그쪽이 더 정확하다.
        if (!_hasThumbnail) LoadCover(extras?.CoverUrl);

        // 연결돼 있지 않으면 좋아요 자리를 아예 비운다 — 눌러도 안 되는 버튼은 헷갈리게 한다.
        var connected = extras is { LoveConnected: true };
        _love.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (connected)
        {
            var on = extras!.ShowLoved;
            _love.Content = Loc.T(on ? "mini.love.on" : "mini.love.off");
            _love.Foreground = new SolidColorBrush(on ? Heart : Ink);
        }

        _coverRetry.Visibility = _a.RefreshCover is null ? Visibility.Collapsed : Visibility.Visible;
        _meaning.Visibility = _a.OpenMeaning is null ? Visibility.Collapsed : Visibility.Visible;
    }

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

        var band = BottomBandHeight(source);
        // 정사각보다 높으면(다른 앱) 남는 높이도 함께 잘라 정사각으로 맞춘다.
        var overflow = Math.Max(0, height - width);
        var cut = Math.Max(band, overflow);

        Log.Write($"[cover] 재생 앱 표지 {width}x{height}, 아래 띠 {band}px");
        if (cut <= 0 || cut >= height) return source;

        try
        {
            var cropped = new CroppedBitmap(source, new Int32Rect(0, 0, width, height - cut));
            cropped.Freeze();
            return cropped;
        }
        catch (Exception)
        {
            return source; // 자르기 실패는 원본 그대로(로고가 보이는 편이 빈 창보다 낫다)
        }
    }

    /// <summary>
    /// 아래쪽 단색 띠의 높이(없으면 0). 각 행의 <b>양쪽 끝</b> 픽셀만 본다 — 로고가 가운데
    /// 있어도 끝은 배경색이기 때문이다.
    /// </summary>
    private static int BottomBandHeight(BitmapSource source)
    {
        try
        {
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var limit = height / 4;              // 높이의 25%까지만 띠로 인정한다
            if (width < 8 || limit < 2) return 0;

            var bgra = new BitmapImageConverter(source);
            var corner = bgra.At(0, height - 1);

            var band = 0;
            for (var y = height - 1; y >= height - limit; y--)
            {
                if (!Near(bgra.At(0, y), corner) || !Near(bgra.At(width - 1, y), corner)) break;
                band++;
            }

            // 맨 아래 한두 줄이 같은 건 흔하다 — 띠라고 부를 만한 두께가 아니면 0.
            return band >= height / 50 && band >= 4 ? band : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>두 색이 눈으로 같은가(채널별 차이 합으로 본다).</summary>
    private static bool Near((byte B, byte G, byte R) a, (byte B, byte G, byte R) b) =>
        Math.Abs(a.B - b.B) + Math.Abs(a.G - b.G) + Math.Abs(a.R - b.R) <= 12;

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
        _cover.Source = null;
        if (string.IsNullOrWhiteSpace(url)) return;

        var epoch = _extrasEpoch;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (epoch != _extrasEpoch) return;
            ShowThumbnail(bytes);
        }
        catch (Exception)
        {
            // 대체 배경이 이미 깔려 있다 — 창이 깨지지 않는다.
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

    private static Button MediaButton(Action onClick, bool primary = false) =>
        Chip(onClick, primary, size: 38);

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
            _artist.Text = _restArtist.Text = "";
            _artist.Visibility = _restArtist.Visibility = Visibility.Collapsed;
            _extrasEpoch++;          // 진행 중인 조회 결과를 버린다
            ApplyExtras(null);
            return;
        }

        _title.Text = _restTitle.Text = title;
        _artist.Text = _restArtist.Text = artist ?? "";
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        _artist.Visibility = _restArtist.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        FetchExtras();
    }

    /// <summary>오버레이 표시 상태에 맞춰 토글 버튼 라벨을 갱신한다(트레이와 동기화).</summary>
    public void SyncOverlayVisible(bool visible) =>
        _overlayToggle.Content = Loc.T(visible ? "mini.hideOverlay" : "mini.showOverlay");

    /// <summary>재생 상태·컨트롤 가용성에 맞춰 재생 컨트롤 행을 갱신한다.</summary>
    public void RefreshPlayback()
    {
        var c = _a.GetControls();
        var playing = _a.IsPlaying();
        _prev.Content = Loc.T("mini.control.prev");
        _next.Content = Loc.T("mini.control.next");
        _playPause.Content = Loc.T(playing ? "mini.control.pause" : "mini.control.play");
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
        _settings.Content = Loc.T("mini.settings");
        _exit.Content = Loc.T("mini.exit");
        _search.Content = Loc.T("mini.search");
        _openLyrics.Content = Loc.T("mini.openLyrics");
        _wrong.Content = Loc.T("mini.wrong");
        _offsetMinus.Content = Loc.T("mini.offset.minus");
        _offsetPlus.Content = Loc.T("mini.offset.plus");
        _offsetReset.Content = Loc.T("mini.offset.reset");
        _coverRetry.Content = Loc.T("mini.cover");
        _minimize.Content = "—";
        _close.Content = "✕";
        ApplyMeaningLabel();
        _love.Content = Loc.T(_extras is { } e && e.ShowLoved ? "mini.love.on" : "mini.love.off");
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
