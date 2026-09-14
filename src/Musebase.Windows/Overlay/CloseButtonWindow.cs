using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Musebase.Windows.Services;

namespace Musebase.Windows.Overlay;

/// <summary>
/// 오버레이 우상단의 <b>숨기기</b> 버튼.
///
/// 지금까지 오버레이 위에서 오버레이를 직접 숨길 방법이 없었다 — 트레이 메뉴나 제어판을
/// 찾아가야 했다(우상단 자물쇠는 이동 모드 토글일 뿐이다). 가사가 지금 거슬리는 그 자리에서
/// 바로 치울 수 있어야 한다.
///
/// 되살리는 길은 그대로다: 트레이 메뉴 · 작업표시줄 점프 목록 · 제어판의 오버레이 버튼.
/// <see cref="LockButtonWindow"/>·<see cref="PanelButtonWindow"/>와 같은 형태의 소형 창이다 —
/// 오버레이 본체는 클릭스루라 입력을 못 받는다.
/// </summary>
public sealed class CloseButtonWindow : Window
{
    private static readonly SolidColorBrush IdleBackground = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

    /// <summary>호버하면 붉게 — 누르면 사라진다는 뜻을 색으로 미리 알린다(창 닫기 버튼의 관행).</summary>
    private static readonly SolidColorBrush HoverBackground = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));

    private static readonly SolidColorBrush IdleStroke = Freeze(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush HoverStroke = Freeze(Colors.White);

    private readonly Border _chrome;
    private readonly Path _cross;

    public CloseButtonWindow(Action onClose)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 32;
        Height = 32;

        // 24×24 뷰박스에 ✕ 한 획. 선 스타일이라 자물쇠·제어판 버튼과 같은 무게로 보인다.
        _cross = new Path
        {
            Data = Freeze(Geometry.Parse("M 8,8 L 16,16 M 16,8 L 8,16")),
            Stroke = IdleStroke,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_cross);

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
        ToolTip = Loc.T("overlay.close");

        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClose();
        };
        MouseEnter += (_, _) =>
        {
            _chrome.Background = HoverBackground;
            _cross.Stroke = HoverStroke;
        };
        MouseLeave += (_, _) =>
        {
            _chrome.Background = IdleBackground;
            _cross.Stroke = IdleStroke;
        };

        SourceInitialized += (_, _) =>
        {
            // 포커스 훔치지 않기 + Alt-Tab 목록 제외 (클릭스루는 아님!)
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
        };
    }

    /// <summary>언어를 바꾸면 설명도 따라간다.</summary>
    public void ApplyText() => ToolTip = Loc.T("overlay.close");

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
