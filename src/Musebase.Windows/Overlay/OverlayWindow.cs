using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Musebase.Windows.Services;
using Musebase.Core;
using Musebase.Engine;

namespace Musebase.Windows.Overlay;

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
    private readonly Border _backgroundBorder; // 라운드 배경판(창은 투명 유지, 배경은 이 Border가 담당)
    private readonly LockButtonWindow _lockButton;
    private readonly DispatcherTimer _hoverTimer;

    // 좌상단 제어판 버튼(마우스 오버 시 표시). EnablePanelButton으로 배선되면 생성된다.
    // 예전에는 여기에 재생 컨트롤이 있었는데, 같은 기능이 제어판에도 있어 자리만 두 벌 썼다.
    private PanelButtonWindow? _panelButton;
    private CloseButtonWindow? _closeButton;
    private Func<bool>? _panelVisibleProvider;

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(180));

    private bool _clickThrough = true;
    private bool _userVisible = true;
    private bool _fullscreenSuppressed;
    private bool _pausedSuppressed;
    // 표시할 글자가 없는 구간(전주·간주 — LRC의 공백뿐인 줄 포함)에는 빈 배경판만 남으므로 숨긴다.
    private bool _emptyLineSuppressed = true;
    private bool _mouseOverSuppressed; // 마우스 오버 시 숨김(설정)
    private string _shownContent = string.Empty; // 페이드 크로스페이드 판단용

    private InlineTimeTags? _karaoke; // 현재 라인의 글자단위 태그 (null = 라인 단위 폴백)
    private double _lineSpan;          // 현재 라인 표시 구간(초)
    private bool _translationSuppressedAsSame; // 번역이 원문과 같아 숨긴 상태(폰트 크기 유지용)

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
        _panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _panel.Children.Add(_originalLine);
        _panel.Children.Add(_translationLine);
        // 투명 창 + 내부 Border(라운드 배경). 창 자체는 항상 투명(히트테스트/클릭스루 유지),
        // 배경 반투명판·모서리 라운드는 이 Border가 담당한다.
        _backgroundBorder = new Border
        {
            Child = _panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Content = new Grid { Children = { _backgroundBorder } };
        ApplyStyle();

        _lockButton = new LockButtonWindow(() => SetMoveMode(!IsMoveMode));
        _lockButton.SetLocked(true);

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _hoverTimer.Tick += (_, _) => OnHoverTick();

        Loaded += (_, _) =>
        {
            // 소유 창은 항상 소유자 위에 유지됨 — 이동 모드에서 오버레이를
            // 드래그해도 자물쇠 버튼이 아래로 깔려 클릭 불능이 되지 않는다
            _lockButton.Owner = this;
            if (_panelButton is not null) _panelButton.Owner = this;
            if (_closeButton is not null) _closeButton.Owner = this;
            RestorePosition();
            UpdateTextLayout();
            _hoverTimer.Start();
        };
        SizeChanged += (_, _) => UpdateTextLayout();
        LocationChanged += (_, _) => UpdateOverlayButtons();
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
            _panelButton?.Close();
        };
    }

    /// <summary>
    /// 좌상단의 제어판 여닫기 버튼을 활성화한다. 마우스 오버 시에만 표시된다.
    /// <paramref name="visibleProvider"/>는 갱신마다 제어판이 지금 떠 있는지를 물어 색·설명에 반영한다.
    /// </summary>
    public void EnablePanelButton(Func<bool> visibleProvider, Action onToggle)
    {
        _panelVisibleProvider = visibleProvider;
        _panelButton = new PanelButtonWindow(onToggle);
        if (IsLoaded) _panelButton.Owner = this;
    }

    /// <summary>
    /// 우상단 숨기기(✕) 버튼을 활성화한다. 지금까지 오버레이 위에서 오버레이를 직접 치울
    /// 방법이 없어 트레이나 제어판을 찾아가야 했다.
    /// </summary>
    public void EnableCloseButton(Action onClose)
    {
        _closeButton = new CloseButtonWindow(onClose);
        if (IsLoaded) _closeButton.Owner = this;
    }

    // ---- 표시 내용 ----

    public void SetLine(DisplayLine? line)
    {
        var newContent = line?.Content ?? string.Empty;
        // 페이드 옵션: 내용이 실제로 바뀔 때만 크로스페이드(진행 갱신은 SetProgress가 처리)
        if (_settings.FadeAnimation && IsLoaded && newContent != _shownContent)
        {
            var fadeOut = new DoubleAnimation { From = _panel.Opacity, To = 0, Duration = FadeDuration };
            fadeOut.Completed += (_, _) =>
            {
                ApplyLineContent(line);
                _shownContent = newContent;
                _panel.BeginAnimation(OpacityProperty, new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration });
            };
            _panel.BeginAnimation(OpacityProperty, fadeOut);
            return;
        }

        _panel.BeginAnimation(OpacityProperty, null);
        _panel.Opacity = 1;
        ApplyLineContent(line);
        _shownContent = newContent;
    }

    private void ApplyLineContent(DisplayLine? line)
    {
        _originalLine.Text = line?.Content ?? string.Empty;
        // 설정이 켜져 있고 라인에 글자단위 태그가 있을 때만 글자 채움, 아니면 라인 단위 폴백
        _karaoke = _settings.CharacterKaraoke ? line?.Karaoke : null;
        _lineSpan = line?.LineSpanSeconds ?? 0;
        _originalLine.InlineKaraoke = _karaoke;
        _originalLine.KaraokeProgress = 0;
        _originalLine.KaraokeTime = 0;

        // 옵션: 번역이 원문과 같으면 번역 줄을 숨겨 원문만 표시.
        // 단, 이 경우에도 원문 폰트는 '번역 있을 때' 크기를 유지한다(레이아웃 튐 방지).
        var rawTranslation = line?.Translation;
        _translationSuppressedAsSame = _settings.HideSameTranslation
            && rawTranslation is not null && line?.Content is { } content
            && string.Equals(rawTranslation.Trim(), content.Trim(), StringComparison.OrdinalIgnoreCase);
        var translation = _translationSuppressedAsSame ? null : rawTranslation;

        _translationLine.Text = translation ?? string.Empty;
        _translationLine.Visibility = string.IsNullOrWhiteSpace(translation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateTextLayout();

        // 원문이 비었거나 공백뿐이면(간주 표시 줄 등) 글자 없는 배경판만 남는다 — 그 구간은 숨긴다.
        // (Android 오버레이와 같은 규칙. 이동 모드에서는 ShouldBeVisible이 억제를 무시한다.)
        _emptyLineSuppressed = string.IsNullOrWhiteSpace(line?.Content);
        ApplyVisibility();
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
        // 동일 번역 숨김으로 가려진 경우에도 번역 있을 때 크기를 유지
        var reserveForTranslation = hasTranslation || _translationSuppressedAsSame;

        _originalLine.FontSize = Math.Max(12, reserveForTranslation ? h * 0.34 : h * 0.44);
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

        // 이동 모드 중에는 어두운 안내 배경을 라운드 판에 칠하고, 벗어나면 설정 배경으로 복원.
        _backgroundBorder.Background = moveMode
            ? new SolidColorBrush(Color.FromArgb(0x50, 0x20, 0x20, 0x20))
            : ComputeBackgroundBrush();
        if (moveMode && string.IsNullOrWhiteSpace(_originalLine.Text))
        {
            _originalLine.Text = Loc.T("overlay.moveHint");
            UpdateTextLayout();
        }

        _lockButton.SetLocked(!moveMode);
        if (!moveMode) SaveBounds();
        MoveModeChanged?.Invoke(moveMode);
        ApplyVisibility(); // 억제 상태여도 이동 모드 진입 시 표시
        UpdateOverlayButtons();
    }

    /// <summary>호버 타이머(150ms): 마우스 오버 숨김 처리 + 자물쇠 버튼 표시 갱신.</summary>
    private void OnHoverTick()
    {
        // 마우스 오버 시 숨김 옵션 — 이동 모드 중에는 무시(사용자가 조작 중)
        if (_settings.HideOnMouseOver && !IsMoveMode)
        {
            var over = IsCursorOverOverlay();
            if (over != _mouseOverSuppressed)
            {
                _mouseOverSuppressed = over;
                ApplyVisibility();
            }
        }
        else if (_mouseOverSuppressed)
        {
            _mouseOverSuppressed = false;
            ApplyVisibility();
        }

        UpdateOverlayButtons();
    }

    /// <summary>
    /// 버튼 세 개의 표시 여부와 자리를 한 번에 정한다 —
    /// 제어판(좌하단) · 자물쇠(우하단) · 숨기기(우상단).
    ///
    /// 예전에는 버튼마다 판단과 좌표가 따로 있어, 배치를 바꿀 때 한쪽만 고치면 서로 겹쳤다.
    ///
    /// 규칙은 셋이다. ① 오버레이가 안 보이면 전부 숨긴다. ② <b>이동 모드에서는 자물쇠만</b>
    /// 남긴다 — 자리를 잡는 중에 숨기기·제어판을 누를 일이 없고, 오히려 잘못 눌러 사라진다.
    /// ③ 그 밖에는 커서가 오버레이나 버튼 위에 있을 때만 보인다.
    /// </summary>
    private void UpdateOverlayButtons()
    {
        if (!IsVisible)
        {
            HideOverlayButtons();
            return;
        }

        if (IsMoveMode)
        {
            _panelButton?.Hide();
            _closeButton?.Hide();
            PositionLock();
            return;
        }

        var overButton = _lockButton.IsMouseOver
            || _panelButton is { IsMouseOver: true }
            || _closeButton is { IsMouseOver: true };

        if (!overButton && !IsCursorOverOverlay())
        {
            HideOverlayButtons();
            return;
        }

        if (_panelVisibleProvider is { } panelVisible) _panelButton?.SetPanelVisible(panelVisible());

        PositionLock();
        _panelButton?.ShowAt(Left + ButtonGap, Bottom - ButtonSize - ButtonGap);
        _closeButton?.ShowAt(Right - ButtonSize - ButtonGap, Top + ButtonGap);
    }

    private void PositionLock() =>
        _lockButton.ShowAt(Right - ButtonSize - ButtonGap, Bottom - ButtonSize - ButtonGap);

    private void HideOverlayButtons()
    {
        _lockButton.Hide();
        _panelButton?.Hide();
        _closeButton?.Hide();
    }

    /// <summary>세 버튼 창의 한 변(모두 같다)과 오버레이 모서리에서 띄우는 간격.</summary>
    private const double ButtonSize = 32;
    private const double ButtonGap = 6;

    private double Right => Left + ActualWidth;
    private double Bottom => Top + ActualHeight;

    // 오버레이의 화면 영역(물리 px). 보이는 동안 갱신해 두면 '마우스 오버 시 숨김'으로
    // 창이 숨겨진 뒤에도(PresentationSource 변환에 의존하지 않고) 커서 이탈을 판정할 수 있다.
    private Rect _cachedScreenBounds = Rect.Empty;

    private bool IsCursorOverOverlay()
    {
        if (IsVisible && PresentationSource.FromVisual(this) is not null)
        {
            try
            {
                var tl = PointToScreen(new Point(0, 0));
                var br = PointToScreen(new Point(ActualWidth, ActualHeight));
                _cachedScreenBounds = new Rect(tl, br);
            }
            catch
            {
                // PresentationSource 미준비 — 마지막 캐시 사용
            }
        }

        if (_cachedScreenBounds.IsEmpty) return false;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        return _cachedScreenBounds.Contains(pt.X, pt.Y);
    }



    // ---- 가시성 (사용자 토글 × 전체화면 억제) ----

    public void SetUserVisible(bool visible)
    {
        _userVisible = visible;
        ApplyVisibility();
    }

    /// <summary>
    /// 미니창(작업표시줄)에서 오버레이 되살리기: <b>사용자 숨김</b>과 마우스오버 억제를 푼다.
    ///
    /// <b>일시정지 억제는 건드리지 않는다.</b> 예전에는 여기서 false로 덮어썼는데, 재계산이
    /// 재생 상태 변화로만 일어나 <b>멈춘 가사가 다음 재생까지 떠 있었다</b> — 제어판을 여는
    /// 것이 "일시정지 중에도 보이게 해 달라"는 뜻일 리 없다.
    ///
    /// 전체화면 억제도 그대로 둔다(게임·영상 위로 튀지 않게).
    /// </summary>
    public void ReviveVisible()
    {
        _userVisible = true;
        _mouseOverSuppressed = false;
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

    /// <summary>이동 모드 중에는 억제(전체화면/일시정지/마우스오버)를 무시 — 사용자가 조작 중.</summary>
    private bool ShouldBeVisible() =>
        _userVisible
        && (IsMoveMode
            || (!_fullscreenSuppressed && !_pausedSuppressed && !_mouseOverSuppressed && !_emptyLineSuppressed));

    private void ApplyVisibility()
    {
        if (ShouldBeVisible()) ShowOverlay();
        else HideOverlay();
    }

    private void ShowOverlay()
    {
        if (_settings.FadeAnimation)
        {
            if (!IsVisible) { Opacity = 0; Show(); }
            BeginAnimation(OpacityProperty, new DoubleAnimation { From = Opacity, To = 1, Duration = FadeDuration });
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Show();
        }
    }

    private void HideOverlay()
    {
        if (_settings.FadeAnimation && IsVisible)
        {
            var fade = new DoubleAnimation { From = Opacity, To = 0, Duration = FadeDuration };
            // 페이드 도중 다시 표시로 바뀌면 숨기지 않는다(마지막 상태를 재확인).
            fade.Completed += (_, _) => { if (!ShouldBeVisible()) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Hide();
        }
        HideOverlayButtons();
    }

    /// <summary>설정의 색상/외곽선 스타일 적용 (설정 저장 후에도 호출)</summary>
    public void ApplyStyle()
    {
        _originalLine.Fill = ParseBrush(_settings.TextColor, Colors.White);
        _originalLine.KaraokeFill = ParseBrush(_settings.KaraokeColor, Color.FromRgb(0xFF, 0xEB, 0x3B));
        _translationLine.Fill = ParseBrush(_settings.TranslationColor, Color.FromRgb(0xE8, 0xE8, 0xE8));

        var font = FontOf(_settings.OverlayFontFamily);
        _originalLine.FontFamily = font;
        _translationLine.FontFamily = font;

        var outline = ParseBrush(_settings.OutlineColor, Colors.Black, alpha: 0xE0);
        var thickness = Math.Clamp(_settings.OutlineThickness, 0, 8);
        _originalLine.Stroke = outline;
        _originalLine.StrokeThickness = thickness;
        _translationLine.Stroke = outline;
        _translationLine.StrokeThickness = thickness;

        // 라운드 배경판(반투명) — 이동 모드 중에는 그 어두운 배경을 유지
        _backgroundBorder.CornerRadius = new CornerRadius(Math.Max(0, _settings.OverlayCornerRadius));
        if (!IsMoveMode) _backgroundBorder.Background = ComputeBackgroundBrush();

        _originalLine.InvalidateVisual();
        _translationLine.InvalidateVisual();
        if (IsLoaded) UpdateTextLayout(); // 외곽선 두께가 측정 크기에 영향
    }

    /// <summary>설정의 배경 색·불투명도로 브러시 생성(비활성 시 투명).</summary>
    private Brush ComputeBackgroundBrush()
    {
        if (!_settings.OverlayBackgroundEnabled) return Brushes.Transparent;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(_settings.OverlayBackgroundColor); }
        catch { color = Colors.Black; }
        color.A = (byte)(Math.Clamp(_settings.OverlayBackgroundOpacity, 0.0, 1.0) * 255);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 설정한 글꼴 이름을 <see cref="FontFamily"/>로. <b>폴백을 함께 넣는다</b> —
    /// 이름을 직접 칠 수 있게 해 두었으므로 오타나 다른 PC에서 없는 글꼴이 들어올 수 있는데,
    /// WPF는 그때 글꼴을 못 찾으면 기본 글꼴로 내려간다. 폴백을 명시해 두면 그 결과가 예측 가능하다.
    /// </summary>
    public static FontFamily FontOf(string? name)
    {
        var wanted = (name ?? "").Trim();
        return wanted.Length == 0 ? new FontFamily("Segoe UI") : new FontFamily($"{wanted}, Segoe UI");
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
            // 설치 직후 기본 자리 — 주 모니터 작업영역의 가로 중앙, 작업표시줄 위.
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 64;
        }
        KeepOnScreen();
        UpdateOverlayButtons();
    }

    private void SaveBounds()
    {
        _settings.OverlayX = Left;
        _settings.OverlayY = Top;
        _settings.OverlayWidth = ActualWidth;
        _settings.OverlayHeight = ActualHeight;
        _settings.Save();
    }

    /// <summary>
    /// 창을 <b>그 창이 놓인 모니터의 작업영역</b> 안으로 되돌린다.
    ///
    /// 예전에는 가상 화면(VirtualScreen) 기준이라 작업표시줄 아래로 끼는 것을 막지 못했다 —
    /// 그러면 가사 아래쪽이 작업표시줄에 가려 읽히지 않는다. 모니터를 뗀 뒤의 복원도 여기서 잡힌다.
    /// </summary>
    private void KeepOnScreen()
    {
        var area = MonitorWorkArea.For(this);

        if (Left + ActualWidth > area.Right) Left = area.Right - ActualWidth;
        if (Top + ActualHeight > area.Bottom) Top = area.Bottom - ActualHeight;
        if (Left < area.Left) Left = area.Left;
        if (Top < area.Top) Top = area.Top;
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
                // 보정을 먼저 한다 — 반대 순서면 화면 밖으로 끌어 놓은 좌표가 그대로 저장돼,
                // 화면에 보이는 자리와 설정값이 어긋난다.
                KeepOnScreen();
                SaveBounds();
                UpdateOverlayButtons();
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
