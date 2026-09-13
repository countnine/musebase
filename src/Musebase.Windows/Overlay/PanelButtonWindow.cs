using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Musebase.Windows.Services;

namespace Musebase.Windows.Overlay;

/// <summary>
/// 오버레이 좌상단의 <b>제어판 여닫기</b> 버튼.
///
/// 예전에는 이 자리에 재생 컨트롤(이전·재생·다음)이 있었는데, 같은 기능이 제어판에도 있어
/// 자리만 두 벌 쓰고 있었다. 오버레이에서 제어판으로 가는 길은 <b>하나도 없었고</b>
/// (트레이 메뉴·트레이 더블클릭·작업표시줄 점프 목록뿐) 가사를 보다가 뭘 하려면 트레이를 찾아야 했다.
///
/// 우상단 자물쇠(<see cref="LockButtonWindow"/>)와 같은 모양·같은 방식이다 — 오버레이 본체는
/// 클릭스루라 입력을 못 받으므로 클릭 가능한 소형 창을 따로 띄운다.
/// </summary>
public sealed class PanelButtonWindow : Window
{
    private static readonly SolidColorBrush IdleBackground = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush HoverBackground = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    // 열려 있을 때는 강조색(지금 떠 있다), 닫혀 있을 때는 중립 회색 — 자물쇠와 같은 규칙이다.
    private static readonly SolidColorBrush ClosedColor = Freeze(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush OpenColor = Freeze(Color.FromRgb(0x1D, 0xB9, 0x54));

    private readonly Border _chrome;
    private readonly Path _frame;
    private readonly Path _art;

    public PanelButtonWindow(Action onToggle)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 32;
        Height = 32;

        // 24×24 뷰박스. 제어판이 "커버가 꽉 찬 창"이므로 그 모양을 그대로 그린다 —
        // 바깥 테두리 + 안쪽 채운 사각형(앨범 표지) + 아래 한 줄(곡명 판).
        _frame = new Path
        {
            Data = Freeze(new RectangleGeometry(new Rect(4, 5, 16, 14), 2, 2)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _art = new Path
        {
            Data = Freeze(new RectangleGeometry(new Rect(7, 8, 10, 5), 1, 1)),
            StrokeThickness = 0,
        };

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_frame);
        canvas.Children.Add(_art);

        _chrome = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = IdleBackground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Child = new Viewbox
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas,
            },
        };
        Content = _chrome;
        Cursor = Cursors.Hand;

        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onToggle();
        };
        MouseEnter += (_, _) => _chrome.Background = HoverBackground;
        MouseLeave += (_, _) => _chrome.Background = IdleBackground;

        SourceInitialized += (_, _) =>
        {
            // 포커스 훔치지 않기 + Alt-Tab 목록 제외 (클릭스루는 아님!)
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
        };

        SetPanelVisible(false);
    }

    /// <summary>제어판이 지금 떠 있는지에 맞춰 색과 설명을 갱신한다.</summary>
    public void SetPanelVisible(bool visible)
    {
        var color = visible ? OpenColor : ClosedColor;
        _frame.Stroke = color;
        _art.Fill = color;
        ToolTip = Loc.T(visible ? "overlay.panel.hide" : "overlay.panel.show");
    }

    public void ShowAt(double left, double top)
    {
        Left = left;
        Top = top;
        if (!IsVisible) Show();
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
