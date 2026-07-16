using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Musebase.Windows.Services;

namespace Musebase.Windows.Overlay;

/// <summary>
/// 오버레이 우상단에 뜨는 자물쇠 버튼 (이동 모드 토글).
/// 오버레이 본체는 클릭스루라 마우스 입력을 못 받으므로,
/// 클릭 가능한 별도 소형 창으로 띄운다.
/// 아이콘은 단순한 선(라인) 스타일 자물쇠 — 잠금/해제를 걸쇠 모양 + 색으로 구분한다.
/// </summary>
public sealed class LockButtonWindow : Window
{
    private static readonly SolidColorBrush IdleBackground = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush HoverBackground = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    // 잠금 = 중립 회색(고정 상태), 해제 = 녹색(이동 모드 진행 중임을 강조)
    private static readonly SolidColorBrush LockedColor = Freeze(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush UnlockedColor = Freeze(Color.FromRgb(0x1D, 0xB9, 0x54));

    // 24×24 뷰박스 기준. 걸쇠(shackle)만 상태에 따라 바뀌고 몸통·열쇠구멍은 공통.
    private static readonly Geometry ClosedShackle = Freeze(Geometry.Parse("M 8,11 L 8,8 A 4,4 0 0 1 16,8 L 16,11"));
    private static readonly Geometry OpenShackle = Freeze(Geometry.Parse("M 8,11 L 8,7.5 A 4,4 0 0 1 15.8,5.6"));

    private readonly Border _chrome;
    private readonly Path _shackle;
    private readonly Path _body;
    private readonly Ellipse _keyhole;

    public LockButtonWindow(Action onToggle)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 32;
        Height = 32;

        // 몸통(둥근 사각형, 선 스타일) + 걸쇠(호) + 열쇠구멍(점)
        _body = new Path
        {
            Data = new RectangleGeometry(new Rect(6, 11, 12, 9), 2, 2),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _shackle = new Path
        {
            Data = ClosedShackle,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _keyhole = new Ellipse { Width = 2.6, Height = 2.6 };
        Canvas.SetLeft(_keyhole, 12 - 1.3);
        Canvas.SetTop(_keyhole, 15 - 1.3);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_body);
        canvas.Children.Add(_shackle);
        canvas.Children.Add(_keyhole);

        var icon = new Viewbox
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
        _chrome = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = IdleBackground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = icon,
            Margin = new Thickness(2),
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
    }

    /// <summary>잠금 상태 표시 갱신 (닫힌 걸쇠·회색 = 고정/클릭스루, 열린 걸쇠·녹색 = 이동 모드)</summary>
    public void SetLocked(bool locked)
    {
        var color = locked ? LockedColor : UnlockedColor;
        _shackle.Data = locked ? ClosedShackle : OpenShackle;
        _shackle.Stroke = color;
        _body.Stroke = color;
        _keyhole.Fill = color;
        ToolTip = locked ? Loc.T("lock.tooltip.locked") : Loc.T("lock.tooltip.unlocked");
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
