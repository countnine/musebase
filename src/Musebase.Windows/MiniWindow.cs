using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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
    Func<bool> CloseToTray);

/// <summary>
/// 작업표시줄에 상주하는 컨트롤 허브. 오버레이가 숨겨져도(사용자 숨김·일시정지·가림방지)
/// 여기서 항상 되살릴 수 있는 손잡이 역할을 하며, 트레이 없이도 기본 기능을 쓸 수 있다.
/// - 곡 제목/아티스트 + 가사 소스 상태 표시.
/// - 재생 컨트롤(이전/재생·정지/다음), 오프셋 조정, 가사 검색/열기/틀린가사.
/// - 오버레이 표시·설정·종료. 표시 상태·오프셋·컨트롤 활성은 트레이와 동기화.
/// - 닫기(X): 옵션 꺼짐(기본)=최소화(작업표시줄 상주), 켜짐=Hide()로 트레이로 숨김(실제 종료 아님).
/// </summary>
public sealed class MiniWindow : Window
{
    private readonly MiniWindowActions _a;

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

    private bool _closingToExit; // "종료" 경로에서만 실제 닫힘 허용

    public MiniWindow(System.Drawing.Icon? appIcon, MiniWindowActions actions)
    {
        _a = actions;

        Title = Loc.T("mini.title");
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (appIcon is not null)
        {
            try
            {
                Icon = Imaging.CreateBitmapSourceFromHIcon(
                    appIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { /* 아이콘 변환 실패는 무시(기본 아이콘) */ }
        }

        // ---- 1) 곡 제목 + 아티스트 ----
        _title = new TextBlock
        {
            Text = Loc.T("mini.noTrack"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _artist = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };

        // ---- 2) 가사 소스/상태 ----
        _source = new TextBlock
        {
            Text = Loc.T("mini.status.idle"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 12),
            MinHeight = 18,
        };

        // ---- 3) 재생 컨트롤 ----
        _prev = MediaButton(() => _a.OnPrevious());
        _playPause = MediaButton(() => _a.OnPlayPause());
        _next = MediaButton(() => _a.OnNext());
        var playbackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        playbackRow.Children.Add(_prev);
        playbackRow.Children.Add(_playPause);
        playbackRow.Children.Add(_next);

        // ---- 4) 가사 오프셋 조정 ----
        _offsetMinus = SmallButton(() => _a.AdjustOffset(-0.5));
        _offsetPlus = SmallButton(() => _a.AdjustOffset(0.5));
        _offsetReset = SmallButton(() => _a.AdjustOffset(null));
        _offsetLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.85,
        };
        var offsetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10),
        };
        offsetRow.Children.Add(_offsetMinus);
        offsetRow.Children.Add(_offsetPlus);
        offsetRow.Children.Add(_offsetReset);
        offsetRow.Children.Add(_offsetLabel);

        // ---- 5) 가사 기능 ----
        _search = FeatureButton(() => _a.OpenSearch());
        _openLyrics = FeatureButton(() => _a.OpenLyricsEditor());
        _wrong = FeatureButton(() => _a.MarkWrong());
        var lyricsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        lyricsRow.Children.Add(_search);
        lyricsRow.Children.Add(_openLyrics);
        lyricsRow.Children.Add(_wrong);

        // ---- 6) 오버레이/설정/종료 ----
        _overlayToggle = FeatureButton(() => _a.SetOverlayVisible(!_a.IsOverlayVisible()));
        _settings = FeatureButton(() => _a.OpenSettings());
        _exit = FeatureButton(() => { _closingToExit = true; _a.Exit(); });
        var controlRow = new StackPanel { Orientation = Orientation.Horizontal };
        controlRow.Children.Add(_overlayToggle);
        controlRow.Children.Add(_settings);
        controlRow.Children.Add(_exit);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(_title);
        root.Children.Add(_artist);
        root.Children.Add(_source);
        root.Children.Add(playbackRow);
        root.Children.Add(offsetRow);
        root.Children.Add(lyricsRow);
        root.Children.Add(controlRow);
        Content = root;

        ApplyText();
        SyncOverlayVisible(_a.IsOverlayVisible());
        RefreshPlayback();
        RefreshOffset();
        RefreshLyricsFeatures();

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

    private static Button MediaButton(Action onClick)
    {
        var b = new Button
        {
            FontSize = 16,
            Width = 44,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 0, 6, 0),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button SmallButton(Action onClick)
    {
        var b = new Button { Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button FeatureButton(Action onClick)
    {
        var b = new Button { Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>가사 소스/상태 한 줄을 갱신한다(코디네이터 CurrentStatus 현지화 문자열).</summary>
    public void SetStatus(string text) => _source.Text = text;

    /// <summary>곡 제목/아티스트를 갱신한다. 곡 없으면 "재생 없음".</summary>
    public void SetTrack(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            _title.Text = Loc.T("mini.noTrack");
            _artist.Text = "";
            _artist.Visibility = Visibility.Collapsed;
        }
        else
        {
            _title.Text = title;
            _artist.Text = artist ?? "";
            _artist.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
        }
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
