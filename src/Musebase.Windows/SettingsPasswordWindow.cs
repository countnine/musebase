using System.Windows;
using System.Windows.Controls;
using Musebase.Windows.Services;

namespace Musebase.Windows;

/// <summary>
/// 설정 백업의 비밀번호를 받는 작은 창.
///
/// <b>비밀번호가 없으면 백업이 쓸모없다</b> — API 키는 이 PC의 DPAPI로 잠겨 있어 파일을 그냥
/// 옮기면 다른 PC에서 조용히 사라진다. 그래서 내보낼 때 비밀번호로 다시 봉인한다.
///
/// 내보내기에서는 <b>두 번 받아 맞춰 본다</b> — 한 번만 받으면 오타가 그대로 굳어, 나중에 가져올 때
/// 열 방법이 없다(그때는 이미 원본 PC가 없을 수도 있다).
/// </summary>
public sealed class SettingsPasswordWindow : Window
{
    private readonly PasswordBox _first = new() { Width = 240, Margin = new Thickness(0, 2, 0, 8) };
    private readonly PasswordBox _again = new() { Width = 240, Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _error = new()
    {
        Foreground = System.Windows.Media.Brushes.IndianRed,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
        Visibility = Visibility.Collapsed,
    };

    private readonly bool _forExport;

    private SettingsPasswordWindow(bool forExport)
    {
        _forExport = forExport;

        Title = Loc.T(forExport ? "settings.backup.password.export" : "settings.backup.password.import");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16), Width = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T(forExport ? "settings.backup.password.hint" : "settings.backup.password.hint.import"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock { Text = Loc.T("settings.backup.password.label") });
        panel.Children.Add(_first);

        if (forExport)
        {
            panel.Children.Add(new TextBlock { Text = Loc.T("settings.backup.password.again") });
            panel.Children.Add(_again);
        }

        panel.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button
        {
            Content = Loc.T("common.save"),
            IsDefault = true,
            Padding = new Thickness(14, 3, 14, 3),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button
        {
            Content = Loc.T("common.cancel"),
            IsCancel = true,
            Padding = new Thickness(14, 3, 14, 3),
        };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _first.Focus();
    }

    /// <summary>사람이 넣은 비밀번호. 취소면 <c>null</c>, 비우고 확인하면 빈 문자열.</summary>
    public string? Password { get; private set; }

    private void Accept()
    {
        if (_forExport && _first.Password != _again.Password)
        {
            _error.Text = Loc.T("settings.backup.password.mismatch");
            _error.Visibility = Visibility.Visible;
            _again.Clear();
            _again.Focus();
            return;
        }

        Password = _first.Password;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 비밀번호를 묻는다. <c>null</c>이면 취소다 — 빈 문자열(비밀값 없이 진행)과 구별해야 한다.
    /// </summary>
    public static string? Ask(Window owner, bool forExport)
    {
        var window = new SettingsPasswordWindow(forExport) { Owner = owner };
        return window.ShowDialog() == true ? window.Password : null;
    }
}
