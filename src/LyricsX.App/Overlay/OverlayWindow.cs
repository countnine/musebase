using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LyricsX.App.Services;

namespace LyricsX.App.Overlay;

/// <summary>
/// 데스크톱 가사 오버레이 (Spike.Overlay에서 승격).
/// 투명·테두리 없음·항상 위. 기본 클릭스루, 이동 모드에서 드래그 가능.
/// 원문 + 번역 2단, 원문에 카라오케 진행 채움.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly AppSettings _settings;
    private readonly OutlinedTextElement _originalLine;
    private readonly OutlinedTextElement _translationLine;
    private bool _clickThrough = true;

    /// <summary>이동 모드 여부 (true = 드래그 가능, 클릭스루 해제)</summary>
    public bool IsMoveMode => !_clickThrough;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _originalLine = new OutlinedTextElement
        {
            FontSize = settings.FontSize,
            Fill = Brushes.White,
            KaraokeFill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _translationLine = new OutlinedTextElement
        {
            FontSize = settings.TranslationFontSize,
            Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(24, 10, 24, 10),
        };
        panel.Children.Add(_originalLine);
        panel.Children.Add(_translationLine);
        Content = panel;

        Loaded += (_, _) => RestorePosition();
        SourceInitialized += (_, _) =>
            ApplyClickThrough(new WindowInteropHelper(this).Handle, _clickThrough);
        SizeChanged += (_, _) => KeepOnScreen();

        MouseLeftButtonDown += (_, _) =>
        {
            if (_clickThrough) return;
            DragMove();
            _settings.OverlayX = Left;
            _settings.OverlayY = Top;
            _settings.Save();
        };
    }

    /// <summary>현재 라인 갱신 (null이면 숨김 수준으로 비움)</summary>
    public void SetLine(DisplayLine? line)
    {
        _originalLine.Text = line?.Content ?? string.Empty;
        _originalLine.KaraokeProgress = 0;
        _translationLine.Text = line?.Translation ?? string.Empty;
        _translationLine.Visibility = string.IsNullOrEmpty(line?.Translation)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>현재 라인 진행 비율(0~1) — 카라오케 채움</summary>
    public void SetProgress(double progress) => _originalLine.KaraokeProgress = progress;

    /// <summary>이동 모드 토글. 이동 모드에서는 배경을 살짝 표시해 위치를 보여준다.</summary>
    public void SetMoveMode(bool moveMode)
    {
        _clickThrough = !moveMode;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) ApplyClickThrough(hwnd, _clickThrough);
        Background = moveMode
            ? new SolidColorBrush(Color.FromArgb(0x50, 0x20, 0x20, 0x20))
            : Brushes.Transparent;
        if (moveMode && string.IsNullOrEmpty(_originalLine.Text))
            _originalLine.Text = "오버레이 위치를 드래그로 옮기세요";
    }

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
    }

    /// <summary>모니터 구성 변경/크기 변화로 화면 밖에 나가지 않게 보정</summary>
    private void KeepOnScreen()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        if (Left + ActualWidth > virtualRight) Left = virtualRight - ActualWidth;
        if (Top + ActualHeight > virtualBottom) Top = virtualBottom - ActualHeight;
        if (Left < virtualLeft) Left = virtualLeft;
        if (Top < virtualTop) Top = virtualTop;
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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
