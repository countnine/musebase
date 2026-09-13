using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    Func<Task<SongExtras?>>? RefreshCover = null);

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
    /// <summary>커버는 정사각이다 — 창 너비가 곧 커버 한 변이다.</summary>
    private const double ArtSize = 360;

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

    private bool _closingToExit;   // "종료" 경로에서만 실제 닫힘 허용
    private SongExtras? _extras;
    private int _extrasEpoch;      // 곡이 바뀌면 늦게 도착한 응답을 버린다

    public MiniWindow(System.Drawing.Icon? appIcon, MiniWindowActions actions)
    {
        _a = actions;

        Title = Loc.T("mini.title");
        Width = ArtSize;
        Height = ArtSize + 39;      // 제목 표시줄 몫
        ResizeMode = ResizeMode.CanMinimize;
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
        var restStack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        restStack.Children.Add(_restTitle);
        restStack.Children.Add(_restArtist);
        _rest = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Background = Scrim(0.0, 0.88) };
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
        _meaning = Chip(() => _a.OpenMeaning?.Invoke());
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

        var veilGrid = new Grid { Margin = new Thickness(12) };
        veilGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        veilGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        veilGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(top, 0);
        Grid.SetRow(bottom, 2);
        veilGrid.Children.Add(top);
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
    /// 곡이 바뀌면 커버·좋아요를 다시 받아 온다. 서버가 커버를 처음 찾는 곡이면 몇 초 걸리므로
    /// <b>화면을 막지 않고</b> 받는 대로 채운다. 그 사이 곡이 또 바뀌면 늦게 온 응답은 버린다.
    /// </summary>
    private async void FetchExtras()
    {
        if (_a.GetExtras is not { } get) return;

        var epoch = ++_extrasEpoch;
        ApplyExtras(null);

        var extras = await get();
        if (epoch == _extrasEpoch) ApplyExtras(extras);
    }

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
        LoadCover(extras?.CoverUrl);

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

    /// <summary>원격 커버를 건다. 실패하면 조용히 대체 배경으로 돌아간다(창이 깨지면 안 된다).</summary>
    private void LoadCover(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _cover.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.DownloadFailed += (_, _) => _cover.Source = null;
            bitmap.DecodeFailed += (_, _) => _cover.Source = null;
            _cover.Source = bitmap;
        }
        catch (Exception)
        {
            _cover.Source = null;
        }
    }

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
        _meaning.Content = Loc.T("mini.meaning");
        _coverRetry.Content = Loc.T("mini.cover");
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
