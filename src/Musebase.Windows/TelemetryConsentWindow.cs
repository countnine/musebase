using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Musebase.Windows.Services;

namespace Musebase.Windows;

/// <summary>
/// 텔레메트리 동의 다이얼로그(최초 1회, ADR-0004).
/// 짧은 설명 + 체크박스 2개(① 기본 통계 / ② 품질 리포트 — 둘 다 기본 꺼짐) +
/// "자세히" 링크(TELEMETRY.md) + 확인 버튼. 창을 닫으면(확인 포함) 다시 묻지 않는다(둘 다 꺼짐 유지).
/// </summary>
public sealed class TelemetryConsentWindow : Window
{
    /// <summary>수집 항목 전체 안내 문서.</summary>
    public const string TelemetryDocUrl = "https://github.com/countnine/musebase/blob/master/TELEMETRY.md";

    public TelemetryConsentWindow(AppSettings settings)
    {
        Title = Loc.T("telemetry.consent.title");
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        MaxHeight = SystemParameters.WorkArea.Height;

        var panel = new StackPanel { Margin = new Thickness(16), Width = 410, HorizontalAlignment = HorizontalAlignment.Left };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("telemetry.consent.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // 둘 다 기본 꺼짐(옵트인)
        var basicCheck = new CheckBox
        {
            Content = new TextBlock { Text = Loc.T("telemetry.consent.basic"), TextWrapping = TextWrapping.Wrap },
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var qualityCheck = new CheckBox
        {
            Content = new TextBlock { Text = Loc.T("telemetry.consent.quality"), TextWrapping = TextWrapping.Wrap },
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(basicCheck);
        panel.Children.Add(qualityCheck);

        // "자세히(수집 항목 전체)" — TELEMETRY.md
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        var link = new Hyperlink(new Run(Loc.T("telemetry.consent.details"))) { NavigateUri = new Uri(TelemetryDocUrl) };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
            catch { /* 브라우저 실행 실패는 무시 */ }
            e.Handled = true;
        };
        details.Inlines.Add(link);
        panel.Children.Add(details);

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("telemetry.consent.note"),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var okButton = new Button
        {
            Content = Loc.T("telemetry.consent.ok"),
            Width = 90,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 10, 16, 12),
        };
        okButton.Click += (_, _) =>
        {
            settings.TelemetryBasicEnabled = basicCheck.IsChecked == true;
            settings.TelemetryQualityEnabled = qualityCheck.IsChecked == true;
            if (settings.TelemetryBasicEnabled || settings.TelemetryQualityEnabled)
                TelemetryClient.EnsureClientId(settings);
            Close();
        };

        // 확인이든 X든 한 번 물었으면 다시 묻지 않는다(체크 안 하면 둘 다 꺼짐 = 미수집)
        Closed += (_, _) =>
        {
            settings.TelemetryConsentAsked = true;
            settings.Save();
        };

        var root = new DockPanel();
        DockPanel.SetDock(okButton, Dock.Bottom);
        root.Children.Add(okButton);
        root.Children.Add(panel);
        Content = root;
    }
}
