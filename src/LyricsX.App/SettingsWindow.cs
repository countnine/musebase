using System.Windows;
using System.Windows.Controls;
using LyricsX.App.Services;

namespace LyricsX.App;

/// <summary>
/// 최소 설정 창: DeepL API 키, 대상 언어, 폰트 크기.
/// 저장 시 onSaved 콜백으로 앱에 즉시 반영한다.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] CommonLanguages =
        ["KO", "EN-US", "EN-GB", "JA", "ZH", "ES", "FR", "DE", "PT-BR"];

    public SettingsWindow(AppSettings settings, Action onSaved)
    {
        Title = "LyricsX 설정";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var apiKeyBox = new TextBox { Text = settings.DeeplApiKey ?? "", Margin = new Thickness(0, 2, 0, 10) };
        var langBox = new ComboBox
        {
            IsEditable = true,
            Text = settings.TargetLanguage,
            ItemsSource = CommonLanguages,
            Margin = new Thickness(0, 2, 0, 10),
        };

        var fontLabel = new TextBlock();
        var fontSlider = new Slider
        {
            Minimum = 20, Maximum = 72, Value = settings.FontSize,
            TickFrequency = 2, IsSnapToTickEnabled = true,
        };
        var trFontLabel = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        var trFontSlider = new Slider
        {
            Minimum = 14, Maximum = 48, Value = settings.TranslationFontSize,
            TickFrequency = 2, IsSnapToTickEnabled = true,
        };
        void UpdateFontLabels()
        {
            fontLabel.Text = $"원문 폰트 크기: {fontSlider.Value:0}";
            trFontLabel.Text = $"번역 폰트 크기: {trFontSlider.Value:0}";
        }
        fontSlider.ValueChanged += (_, _) => UpdateFontLabels();
        trFontSlider.ValueChanged += (_, _) => UpdateFontLabels();
        UpdateFontLabels();

        var saveButton = new Button
        {
            Content = "저장",
            Width = 90,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        saveButton.Click += (_, _) =>
        {
            settings.DeeplApiKey = string.IsNullOrWhiteSpace(apiKeyBox.Text) ? null : apiKeyBox.Text.Trim();
            settings.TargetLanguage = string.IsNullOrWhiteSpace(langBox.Text) ? "KO" : langBox.Text.Trim().ToUpperInvariant();
            settings.FontSize = fontSlider.Value;
            settings.TranslationFontSize = trFontSlider.Value;
            settings.Save();
            onSaved();
            Close();
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "DeepL API 키 (비우면 제공자 번역만 사용)",
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(apiKeyBox);
        panel.Children.Add(new TextBlock
        {
            Text = "번역 대상 언어 (DeepL target_lang, 기본 KO)",
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(langBox);
        panel.Children.Add(fontLabel);
        panel.Children.Add(fontSlider);
        panel.Children.Add(trFontLabel);
        panel.Children.Add(trFontSlider);
        panel.Children.Add(saveButton);
        Content = panel;
    }
}
