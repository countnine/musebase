using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LyricsX.App.Services;
using LyricsX.Engine;

namespace LyricsX.App.Overlay;

/// <summary>
/// 오버레이 좌측에 뜨는 재생 컨트롤(이전 / 재생·일시정지 / 다음).
/// 오버레이 본체는 클릭스루라 입력을 못 받으므로, 자물쇠 버튼처럼
/// 클릭 가능한 별도 소형 창으로 띄운다. SMTC 세션을 통해 실제 플레이어를 제어한다.
/// ("좋아요"는 SMTC 표준에 없어 제외 — 이전/재생·정지/다음만 제공)
/// </summary>
public sealed class MediaControlWindow : Window
{
    private static readonly SolidColorBrush ChromeBackground = Freeze(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush IconColor = Freeze(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly SolidColorBrush IconDisabled = Freeze(Color.FromArgb(0x55, 0x2A, 0x2A, 0x2A));
    private static readonly SolidColorBrush ButtonHover = Freeze(Color.FromArgb(0x22, 0x00, 0x00, 0x00));

    // 24×24 뷰박스 기준 채움 아이콘.
    private static readonly Geometry PrevTriangle = Freeze(Geometry.Parse("M 17,6 L 17,18 L 9,12 Z"));
    private static readonly Geometry PrevBar = Freeze(new RectangleGeometry(new Rect(6, 6, 2.4, 12)));
    private static readonly Geometry NextTriangle = Freeze(Geometry.Parse("M 7,6 L 7,18 L 15,12 Z"));
    private static readonly Geometry NextBar = Freeze(new RectangleGeometry(new Rect(15.6, 6, 2.4, 12)));
    private static readonly Geometry PlayTriangle = Freeze(Geometry.Parse("M 8,5.5 L 8,18.5 L 18,12 Z"));
    private static readonly Geometry PauseBars = Freeze(BuildPause());

    private readonly Border _prevButton;
    private readonly Border _playButton;
    private readonly Border _nextButton;
    private readonly Path _prevTri;
    private readonly Path _prevBar;
    private readonly Path _playPath;
    private readonly Path _nextTri;
    private readonly Path _nextBar;

    private bool _canPrev;
    private bool _canPlayPause;
    private bool _canNext;

    public MediaControlWindow(Action onPrevious, Action onPlayPause, Action onNext)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Height = 34;
        Width = 108;
        SizeToContent = SizeToContent.Width;

        _prevTri = new Path { Data = PrevTriangle, Fill = IconColor };
        _prevBar = new Path { Data = PrevBar, Fill = IconColor };
        _prevButton = BuildButton(onPrevious, out var prevCanvas);
        _prevButton.ToolTip = Loc.T("controls.previous");
        prevCanvas.Children.Add(_prevTri);
        prevCanvas.Children.Add(_prevBar);

        _playPath = new Path { Data = PauseBars, Fill = IconColor };
        _playButton = BuildButton(onPlayPause, out var playCanvas);
        _playButton.ToolTip = Loc.T("controls.playPause");
        playCanvas.Children.Add(_playPath);

        _nextTri = new Path { Data = NextTriangle, Fill = IconColor };
        _nextBar = new Path { Data = NextBar, Fill = IconColor };
        _nextButton = BuildButton(onNext, out var nextCanvas);
        _nextButton.ToolTip = Loc.T("controls.next");
        nextCanvas.Children.Add(_nextTri);
        nextCanvas.Children.Add(_nextBar);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_prevButton);
        row.Children.Add(_playButton);
        row.Children.Add(_nextButton);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ChromeBackground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = row,
            Margin = new Thickness(2),
        };

        SourceInitialized += (_, _) =>
        {
            // 포커스 훔치지 않기 + Alt-Tab 제외 (클릭스루는 아님)
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
        };
    }

    private static Border BuildButton(Action onClick, out Canvas canvas)
    {
        canvas = new Canvas { Width = 24, Height = 24 };
        var icon = new Viewbox
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
        var button = new Border
        {
            Width = 32,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Child = icon,
            Cursor = Cursors.Hand,
        };
        button.MouseEnter += (_, _) => button.Background = ButtonHover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    /// <summary>재생 상태에 따라 가운데 버튼을 재생(▶)/일시정지(‖) 아이콘으로 전환.</summary>
    public void SetPlaying(bool playing) =>
        _playPath.Data = playing ? PauseBars : PlayTriangle;

    /// <summary>세션이 지원하는 컨트롤만 진하게(미지원은 흐리게) 표시.</summary>
    public void SetControls(PlaybackControls controls)
    {
        _canPrev = controls.CanPrevious;
        _canPlayPause = controls.CanPlayPause;
        _canNext = controls.CanNext;

        var prevBrush = _canPrev ? IconColor : IconDisabled;
        _prevTri.Fill = prevBrush;
        _prevBar.Fill = prevBrush;
        _playPath.Fill = _canPlayPause ? IconColor : IconDisabled;
        var nextBrush = _canNext ? IconColor : IconDisabled;
        _nextTri.Fill = nextBrush;
        _nextBar.Fill = nextBrush;
    }

    public void ShowAt(double left, double top)
    {
        Left = left;
        Top = top;
        if (!IsVisible) Show();
    }

    private static Geometry BuildPause()
    {
        var group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(new Rect(7.5, 6, 3, 12), 1, 1));
        group.Children.Add(new RectangleGeometry(new Rect(13.5, 6, 3, 12), 1, 1));
        return group;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
