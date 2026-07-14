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

        var fontHint = new TextBlock
        {
            Text = "텍스트 크기는 오버레이 크기에 맞춰 자동 조절됩니다.\n(오버레이에 마우스를 올려 🔒 클릭 → 이동/크기 조절 모드)",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        };

        // ---- 오버레이 스타일 ----
        (TextBox Box, Border Preview, StackPanel Row) MakeColorRow(string label, string value)
        {
            var box = new TextBox { Text = value, Width = 90, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var preview = new Border
            {
                Width = 20, Height = 20,
                CornerRadius = new CornerRadius(4),
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            void UpdatePreview()
            {
                try
                {
                    preview.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(box.Text.Trim()));
                }
                catch
                {
                    preview.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
            box.TextChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(box);
            row.Children.Add(preview);
            return (box, preview, row);
        }

        var textColor = MakeColorRow("원문 색", settings.TextColor);
        var karaokeColor = MakeColorRow("카라오케 진행 색", settings.KaraokeColor);
        var translationColor = MakeColorRow("번역 색", settings.TranslationColor);
        var outlineColor = MakeColorRow("외곽선 색", settings.OutlineColor);

        var outlineLabel = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        var outlineSlider = new Slider
        {
            Minimum = 0, Maximum = 8, Value = settings.OutlineThickness,
            TickFrequency = 0.5, IsSnapToTickEnabled = true,
        };
        void UpdateOutlineLabel() => outlineLabel.Text = $"외곽선 두께: {outlineSlider.Value:0.#}";
        outlineSlider.ValueChanged += (_, _) => UpdateOutlineLabel();
        UpdateOutlineLabel();

        var karaokeCheck = new CheckBox
        {
            Content = "글자 단위 카라오케 (지원 곡: Kugou/QQ 등)",
            IsChecked = settings.CharacterKaraoke,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var saveButton = new Button
        {
            Content = "저장",
            Width = 90,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        static string NormalizeHex(string input, string fallback)
        {
            var t = input.Trim();
            try
            {
                _ = System.Windows.Media.ColorConverter.ConvertFromString(t);
                return t;
            }
            catch
            {
                return fallback;
            }
        }

        saveButton.Click += (_, _) =>
        {
            settings.DeeplApiKey = string.IsNullOrWhiteSpace(apiKeyBox.Text) ? null : apiKeyBox.Text.Trim();
            settings.TargetLanguage = string.IsNullOrWhiteSpace(langBox.Text) ? "KO" : langBox.Text.Trim().ToUpperInvariant();
            settings.TextColor = NormalizeHex(textColor.Box.Text, settings.TextColor);
            settings.KaraokeColor = NormalizeHex(karaokeColor.Box.Text, settings.KaraokeColor);
            settings.TranslationColor = NormalizeHex(translationColor.Box.Text, settings.TranslationColor);
            settings.OutlineColor = NormalizeHex(outlineColor.Box.Text, settings.OutlineColor);
            settings.OutlineThickness = outlineSlider.Value;
            settings.CharacterKaraoke = karaokeCheck.IsChecked == true;
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
        panel.Children.Add(fontHint);
        panel.Children.Add(new TextBlock { Text = "오버레이 스타일", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(textColor.Row);
        panel.Children.Add(karaokeColor.Row);
        panel.Children.Add(translationColor.Row);
        panel.Children.Add(outlineColor.Row);
        panel.Children.Add(outlineLabel);
        panel.Children.Add(outlineSlider);
        panel.Children.Add(karaokeCheck);
        panel.Children.Add(saveButton);
        Content = panel;
    }
}
