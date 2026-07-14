using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LyricsX.App.Services;
using LyricsX.Core;

namespace LyricsX.App.Overlay;

/// <summary>
/// 데스크톱 가사 오버레이.
/// - 투명·테두리 없음·항상 위. 기본은 클릭스루(잠금).
/// - 호버 시 우상단 자물쇠 버튼 → 이동 모드 토글.
/// - 이동 모드: 내부 드래그 = 이동, 가장자리 드래그 = 크기 조절 (WM_NCHITTEST).
/// - 텍스트 크기는 오버레이 높이에 비례, 긴 줄은 폭에 맞춰 자동 축소.
/// - 전체화면 앱 활성 시 자동 숨김 (SetFullscreenSuppressed).
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double ResizeBorder = 10;   // 크기 조절 히트 영역 (DIP)
    private const double ContentPaddingX = 48;

    private readonly AppSettings _settings;
    private readonly OutlinedTextElement _originalLine;
    private readonly OutlinedTextElement _translationLine;
    private readonly StackPanel _panel;
    private readonly LockButtonWindow _lockButton;
    private readonly DispatcherTimer _hoverTimer;

    private bool _clickThrough = true;
    private bool _userVisible = true;
    private bool _fullscreenSuppressed;
    private bool _pausedSuppressed;

    private InlineTimeTags? _karaoke; // 현재 라인의 글자단위 태그 (null = 라인 단위 폴백)
    private double _lineSpan;          // 현재 라인 표시 구간(초)

    /// <summary>이동 모드 여부 (true = 드래그 이동/크기 조절 가능)</summary>
    public bool IsMoveMode => !_clickThrough;

    /// <summary>이동 모드 변경 알림 (트레이 메뉴 체크 동기화용)</summary>
    public event Action<bool>? MoveModeChanged;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.CanResize; // NCHITTEST 리사이즈 허용 (크롬 없음)
        MinWidth = 220;
        MinHeight = 90;
        Width = settings.OverlayWidth;
        Height = settings.OverlayHeight;

        _originalLine = new OutlinedTextElement
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _translationLine = new OutlinedTextElement
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        ApplyStyle();
        _panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _panel.Children.Add(_originalLine);
        _panel.Children.Add(_translationLine);
        Content = new Grid { Children = { _panel } };

        _lockButton = new LockButtonWindow(() => SetMoveMode(!IsMoveMode));
        _lockButton.SetLocked(true);

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _hoverTimer.Tick += (_, _) => UpdateLockButton();

        Loaded += (_, _) =>
        {
            // 소유 창은 항상 소유자 위에 유지됨 — 이동 모드에서 오버레이를
            // 드래그해도 자물쇠 버튼이 아래로 깔려 클릭 불능이 되지 않는다
            _lockButton.Owner = this;
            RestorePosition();
            UpdateTextLayout();
            _hoverTimer.Start();
        };
        SizeChanged += (_, _) => UpdateTextLayout();
        LocationChanged += (_, _) => PositionLockButton();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough(hwnd, _clickThrough);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Closed += (_, _) =>
        {
            _hoverTimer.Stop();
            _lockButton.Close();
        };
    }

    // ---- 표시 내용 ----

    public void SetLine(DisplayLine? line)
    {
        _originalLine.Text = line?.Content ?? string.Empty;
        // 설정이 켜져 있고 라인에 글자단위 태그가 있을 때만 글자 채움, 아니면 라인 단위 폴백
        _karaoke = _settings.CharacterKaraoke ? line?.Karaoke : null;
        _lineSpan = line?.LineSpanSeconds ?? 0;
        _originalLine.InlineKaraoke = _karaoke;
        _originalLine.KaraokeProgress = 0;
        _originalLine.KaraokeTime = 0;
        _translationLine.Text = line?.Translation ?? string.Empty;
        _translationLine.Visibility = string.IsNullOrEmpty(line?.Translation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateTextLayout();
    }

    /// <summary>라인 시작 이후 경과 시간(초). 글자단위 태그가 있으면 글자 위치까지, 없으면 구간 비율로 채운다.</summary>
    public void SetProgress(double elapsedSeconds)
    {
        if (_karaoke is not null)
            _originalLine.KaraokeTime = elapsedSeconds;
        else
            _originalLine.KaraokeProgress = _lineSpan > 0 ? Math.Clamp(elapsedSeconds / _lineSpan, 0.0, 1.0) : 0.0;
    }

    /// <summary>
    /// 텍스트 크기 재계산: 폰트는 오버레이 높이 비례,
    /// 긴 줄은 폭에 맞춰 패널 전체를 균등 축소.
    /// </summary>
    private void UpdateTextLayout()
    {
        var h = ActualHeight > 0 ? ActualHeight : Height;
        var hasTranslation = _translationLine.Visibility == Visibility.Visible;

        _originalLine.FontSize = Math.Max(12, hasTranslation ? h * 0.34 : h * 0.44);
        _translationLine.FontSize = Math.Max(10, h * 0.21);

        // 자연 크기 측정 후 폭 초과분만 축소 (짧은 줄은 확대하지 않음)
        _panel.LayoutTransform = Transform.Identity;
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _panel.DesiredSize.Width;
        var available = (ActualWidth > 0 ? ActualWidth : Width) - ContentPaddingX;
        if (desired > 0 && available > 0 && desired > available)
        {
            var scale = available / desired;
            _panel.LayoutTransform = new ScaleTransform(scale, scale);
        }
    }

    // ---- 이동 모드 / 자물쇠 ----

    public void SetMoveMode(bool moveMode)
    {
        if (IsMoveMode == moveMode) return;
        _clickThrough = !moveMode;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) ApplyClickThrough(hwnd, _clickThrough);

        Background = moveMode
            ? new SolidColorBrush(Color.FromArgb(0x50, 0x20, 0x20, 0x20))
            : Brushes.Transparent;
        if (moveMode && string.IsNullOrEmpty(_originalLine.Text))
        {
            _originalLine.Text = "드래그: 이동 · 가장자리: 크기 조절";
            UpdateTextLayout();
        }

        _lockButton.SetLocked(!moveMode);
        if (!moveMode) SaveBounds();
        MoveModeChanged?.Invoke(moveMode);
        ApplyVisibility(); // 억제 상태여도 이동 모드 진입 시 표시
        UpdateLockButton();
    }

    private void UpdateLockButton()
    {
        if (!IsVisible)
        {
            _lockButton.Hide();
            return;
        }

        // 이동 모드에서는 항상 표시, 잠금 상태에서는 커서가 영역 위일 때만
        var show = IsMoveMode || _lockButton.IsMouseOver || IsCursorOverOverlay();
        if (show) PositionLockButton();
        else _lockButton.Hide();
    }

    private bool IsCursorOverOverlay()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        try
        {
            var p = PointFromScreen(new Point(pt.X, pt.Y));
            return p.X >= 0 && p.X <= ActualWidth && p.Y >= 0 && p.Y <= ActualHeight;
        }
        catch
        {
            return false; // PresentationSource 미준비
        }
    }

    private void PositionLockButton()
    {
        if (!IsVisible) return;
        _lockButton.ShowAt(Left + ActualWidth - _lockButton.Width - 6, Top + 6);
    }

    // ---- 가시성 (사용자 토글 × 전체화면 억제) ----

    public void SetUserVisible(bool visible)
    {
        _userVisible = visible;
        ApplyVisibility();
    }

    public void SetFullscreenSuppressed(bool suppressed)
    {
        _fullscreenSuppressed = suppressed;
        ApplyVisibility();
    }

    /// <summary>재생 일시정지 중 오버레이 숨김</summary>
    public void SetPausedSuppressed(bool suppressed)
    {
        _pausedSuppressed = suppressed;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        // 이동 모드 중에는 억제(전체화면/일시정지)를 무시 — 사용자가 조작 중
        var visible = _userVisible
            && (IsMoveMode || (!_fullscreenSuppressed && !_pausedSuppressed));
        if (visible)
        {
            Show();
        }
        else
        {
            Hide();
            _lockButton.Hide();
        }
    }

    /// <summary>설정의 색상/외곽선 스타일 적용 (설정 저장 후에도 호출)</summary>
    public void ApplyStyle()
    {
        _originalLine.Fill = ParseBrush(_settings.TextColor, Colors.White);
        _originalLine.KaraokeFill = ParseBrush(_settings.KaraokeColor, Color.FromRgb(0x1D, 0xB9, 0x54));
        _translationLine.Fill = ParseBrush(_settings.TranslationColor, Color.FromRgb(0xE8, 0xE8, 0xE8));

        var outline = ParseBrush(_settings.OutlineColor, Colors.Black, alpha: 0xE0);
        var thickness = Math.Clamp(_settings.OutlineThickness, 0, 8);
        _originalLine.Stroke = outline;
        _originalLine.StrokeThickness = thickness;
        _translationLine.Stroke = outline;
        _translationLine.StrokeThickness = thickness;

        _originalLine.InvalidateVisual();
        _translationLine.InvalidateVisual();
        if (IsLoaded) UpdateTextLayout(); // 외곽선 두께가 측정 크기에 영향
    }

    private static SolidColorBrush ParseBrush(string hex, Color fallback, byte? alpha = null)
    {
        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            color = fallback;
        }
        if (alpha is { } a) color.A = a;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---- 위치/크기 영속화 ----

    private void RestorePosition()
    {
        if (_settings.OverlayX is { } x && _settings.OverlayY is { } y)
        {
            Left = x;
            Top = y;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 64;
        }
        KeepOnScreen();
        PositionLockButton();
    }

    private void SaveBounds()
    {
        _settings.OverlayX = Left;
        _settings.OverlayY = Top;
        _settings.OverlayWidth = ActualWidth;
        _settings.OverlayHeight = ActualHeight;
        _settings.Save();
    }

    private void KeepOnScreen()
    {
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;

        if (Left + ActualWidth > vRight) Left = vRight - ActualWidth;
        if (Top + ActualHeight > vBottom) Top = vBottom - ActualHeight;
        if (Left < vLeft) Left = vLeft;
        if (Top < vTop) Top = vTop;
    }

    // ---- Win32 ----

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // 이동 모드: 내부 = 드래그 이동(HTCAPTION), 가장자리 = 크기 조절
            case NativeMethods.WM_NCHITTEST when IsMoveMode:
            {
                Point p;
                try
                {
                    var x = (short)((long)lParam & 0xFFFF);
                    var y = (short)(((long)lParam >> 16) & 0xFFFF);
                    p = PointFromScreen(new Point(x, y));
                }
                catch
                {
                    return IntPtr.Zero;
                }

                var left = p.X < ResizeBorder;
                var right = p.X > ActualWidth - ResizeBorder;
                var top = p.Y < ResizeBorder;
                var bottom = p.Y > ActualHeight - ResizeBorder;

                var hit = (left, right, top, bottom) switch
                {
                    (true, _, true, _) => NativeMethods.HTTOPLEFT,
                    (_, true, true, _) => NativeMethods.HTTOPRIGHT,
                    (true, _, _, true) => NativeMethods.HTBOTTOMLEFT,
                    (_, true, _, true) => NativeMethods.HTBOTTOMRIGHT,
                    (true, _, _, _) => NativeMethods.HTLEFT,
                    (_, true, _, _) => NativeMethods.HTRIGHT,
                    (_, _, true, _) => NativeMethods.HTTOP,
                    (_, _, _, true) => NativeMethods.HTBOTTOM,
                    _ => NativeMethods.HTCAPTION, // 내부 드래그 = 창 이동
                };
                handled = true;
                return hit;
            }

            // 이동/크기 조절 종료 시 저장
            case NativeMethods.WM_EXITSIZEMOVE:
                SaveBounds();
                PositionLockButton();
                KeepOnScreen();
                break;
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

internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_NCHITTEST = 0x0084;
    public const int WM_EXITSIZEMOVE = 0x0232;

    public static readonly IntPtr HTCAPTION = 2;
    public static readonly IntPtr HTLEFT = 10;
    public static readonly IntPtr HTRIGHT = 11;
    public static readonly IntPtr HTTOP = 12;
    public static readonly IntPtr HTTOPLEFT = 13;
    public static readonly IntPtr HTTOPRIGHT = 14;
    public static readonly IntPtr HTBOTTOM = 15;
    public static readonly IntPtr HTBOTTOMLEFT = 16;
    public static readonly IntPtr HTBOTTOMRIGHT = 17;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);
}
