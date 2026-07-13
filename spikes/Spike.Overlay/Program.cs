// M0-S2 + S3: 투명 클릭스루 오버레이 + 이중언어 외곽선 텍스트 렌더 스파이크
//
// 검증 항목:
//   S2 — 테두리 없는 투명 창, 항상 위, 클릭스루(WS_EX_TRANSPARENT),
//        전역 핫키(Ctrl+Alt+D)로 드래그 모드 전환, 드래그 이동.
//   S3 — 원문+번역 2단 텍스트를 외곽선/그림자 포함 렌더,
//        현재 줄 카라오케 채움(진행 그라디언트) 데모.
//
// 실행: dotnet run -- [지속시간(초), 기본 무제한]

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Spike.Overlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new OverlayWindow();

        if (args.Length > 0 && int.TryParse(args[0], out var seconds) && seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => app.Shutdown();
            timer.Start();
        }

        app.Run(window);
    }
}

internal sealed class OverlayWindow : Window
{
    private const int HotkeyId = 0xB00B;
    private bool _clickThrough = true;
    private readonly OutlinedTextElement _originalLine;
    private readonly OutlinedTextElement _translationLine;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _originalLine = new OutlinedTextElement
        {
            Text = "夜に駆ける 二人だけの世界へ",
            FontSize = 42,
            Fill = Brushes.White,
            KaraokeFill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _translationLine = new OutlinedTextElement
        {
            Text = "밤을 달려, 둘만의 세계로 — Racing into the night",
            FontSize = 27,
            Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var panel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Margin = new Thickness(24, 12, 24, 12),
        };
        panel.Children.Add(_originalLine);
        panel.Children.Add(_translationLine);
        Content = panel;

        Loaded += (_, _) =>
        {
            // 주 모니터 하단 중앙 배치
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 64;

            // 카라오케 채움 데모: 6초 주기로 0→100% 진행 반복
            var fill = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(6))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _originalLine.BeginAnimation(OutlinedTextElement.KaraokeProgressProperty, fill);
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough(hwnd, _clickThrough);
            NativeMethods.RegisterHotKey(hwnd, HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, (uint)'D');
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };

        Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(hwnd, HotkeyId);
        };

        MouseLeftButtonDown += (_, _) =>
        {
            if (!_clickThrough) DragMove();
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _clickThrough = !_clickThrough;
            ApplyClickThrough(hwnd, _clickThrough);
            // 드래그 모드일 때만 위치 파악용 배경 살짝 표시
            Background = _clickThrough
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(0x50, 0x20, 0x20, 0x20));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyClickThrough(IntPtr hwnd, bool enabled)
    {
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        if (enabled) style |= NativeMethods.WS_EX_TRANSPARENT;
        else style &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
    }
}

/// <summary>
/// 외곽선 + 그림자 + 카라오케 진행 채움을 지원하는 텍스트 요소.
/// FormattedText.BuildGeometry로 글리프 지오메트리를 얻어
/// (1) 외곽선+기본색 → (2) 진행 클립 적용한 강조색 순으로 그린다.
/// </summary>
internal sealed class OutlinedTextElement : FrameworkElement
{
    public static readonly DependencyProperty KaraokeProgressProperty =
        DependencyProperty.Register(nameof(KaraokeProgress), typeof(double), typeof(OutlinedTextElement),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 32;
    public Brush Fill { get; set; } = Brushes.White;
    public Brush? KaraokeFill { get; set; }
    public Brush Stroke { get; set; } = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x00, 0x00));
    public double StrokeThickness { get; set; } = 3.0;

    public double KaraokeProgress
    {
        get => (double)GetValue(KaraokeProgressProperty);
        set => SetValue(KaraokeProgressProperty, value);
    }

    private FormattedText? _formatted;

    public OutlinedTextElement()
    {
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 6,
            ShadowDepth = 1.5,
            Opacity = 0.85,
        };
    }

    private FormattedText BuildText() => _formatted ??= new FormattedText(
        Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
        FontSize,
        Fill,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = BuildText();
        return new Size(ft.WidthIncludingTrailingWhitespace + StrokeThickness * 2, ft.Height + StrokeThickness * 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var ft = BuildText();
        var origin = new Point(StrokeThickness, StrokeThickness);
        var geometry = ft.BuildGeometry(origin);
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };

        // 1) 외곽선 + 기본 채움
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawGeometry(Fill, null, geometry);

        // 2) 카라오케 진행 채움 (왼쪽부터 progress 비율만큼 클립)
        if (KaraokeFill is not null && KaraokeProgress > 0)
        {
            var clipWidth = (ft.WidthIncludingTrailingWhitespace + StrokeThickness * 2) * Math.Min(KaraokeProgress, 1.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, clipWidth, RenderSize.Height)));
            dc.DrawGeometry(KaraokeFill, null, geometry);
            dc.Pop();
        }
    }
}

internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
