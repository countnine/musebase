using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using Musebase.Core.Search;
using Musebase.Engine;
using Musebase.Windows.Services;

namespace Musebase.Windows;

/// <summary>
/// "이 곡의 의미" 창. 가사 서버가 미리 만들어 둔 문단을 **읽기만 한다** —
/// 생성은 서버 관리자 화면에서만 일어나므로(쿼타·비용을 사람이 통제) 앱은 조회 전용이다.
///
/// 서버가 없거나 그 곡에 의미가 없으면 그냥 안내 한 줄로 끝난다 — 가사 기능에는 아무 영향이 없다.
/// 출처 표기는 <b>의무</b>다(Wikipedia CC BY-SA 등) — 본문만 떼어 보여 주지 않는다.
/// </summary>
public sealed class MeaningWindow : Window
{
    private readonly LyricsCoordinator _coordinator;
    private readonly TextBlock _header;
    private readonly TextBlock _body;
    private readonly TextBlock _credit;
    private CancellationTokenSource? _cts;

    public MeaningWindow(LyricsCoordinator coordinator)
    {
        _coordinator = coordinator;

        Title = Loc.T("meaning.title");
        Width = 520;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _header = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        _body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Text = Loc.T("meaning.loading"),
        };

        _credit = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(_header);
        stack.Children.Add(_body);
        stack.Children.Add(_credit);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (_coordinator.CurrentTrack is not { } track)
        {
            _body.Text = Loc.T("meaning.noTrack");
            return;
        }

        _header.Text = $"{track.Title} — {track.Artist}";

        if (_coordinator.RemoteCache is not { } remote)
        {
            _body.Text = Loc.T("meaning.noServer");
            return;
        }

        var meaning = await remote.GetMeaningAsync(track.Title, track.Artist, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        if (meaning is null)
        {
            // 대부분의 곡에는 아직 의미가 없다 — 실패가 아니라 정상이다.
            _body.Text = Loc.T("meaning.none");
            return;
        }

        _body.Text = meaning.Summary;
        ShowCredits(meaning);
    }

    /// <summary>출처를 이름·링크로 붙인다. 링크가 있으면 눌러서 원문으로 갈 수 있게 한다.</summary>
    private void ShowCredits(SongMeaningView meaning)
    {
        if (meaning.Attribution.Count == 0) return;

        _credit.Inlines.Clear();
        _credit.Inlines.Add(new Run(Loc.T("meaning.credit") + " "));

        var first = true;
        foreach (var credit in meaning.Attribution)
        {
            if (!first) _credit.Inlines.Add(new Run(" · "));
            first = false;

            if (Uri.TryCreate(credit.Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                var link = new Hyperlink(new Run(credit.Name)) { NavigateUri = uri };
                link.RequestNavigate += OpenExternal;
                _credit.Inlines.Add(link);
            }
            else
            {
                _credit.Inlines.Add(new Run(credit.Name));
            }
        }

        // Wikipedia 본문은 CC BY-SA다 — 이름만으로는 부족하고 라이선스를 함께 밝혀야 한다.
        if (meaning.Attribution.Any(a => a.Name == "Wikipedia"))
            _credit.Inlines.Add(new Run(" (CC BY-SA)"));
    }

    private static void OpenExternal(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"[meaning] 링크 열기 실패: {ex.Message}");
        }
        e.Handled = true;
    }
}
