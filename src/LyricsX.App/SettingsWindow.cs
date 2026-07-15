using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LyricsX.App.Services;

namespace LyricsX.App;

/// <summary>
/// 최소 설정 창: DeepL API 키, 대상 언어, 오버레이 스타일.
/// 저장 시 onSaved 콜백으로 앱에 즉시 반영한다.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] CommonLanguages =
        ["KO", "EN-US", "EN-GB", "JA", "ZH", "ES", "FR", "DE", "PT-BR"];

    // 색 선택 팔레트(4행 × 8열)
    private static readonly string[] PaletteColors =
    [
        "#FFFFFF", "#E8E8E8", "#C0C0C0", "#808080", "#404040", "#000000", "#1DB954", "#1ED760",
        "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3", "#03A9F4", "#00BCD4",
        "#009688", "#4CAF50", "#8BC34A", "#CDDC39", "#FFEB3B", "#FFC107", "#FF9800", "#FF5722",
        "#795548", "#607D8B", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#00FFFF", "#FF00FF",
    ];

    private static SolidColorBrush BrushFromHex(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.Transparent); }
    }

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
                Width = 24, Height = 24,
                CornerRadius = new CornerRadius(4),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "클릭하여 색 선택",
            };
            void UpdatePreview()
            {
                try
                {
                    preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(box.Text.Trim()));
                }
                catch
                {
                    preview.Background = Brushes.Transparent;
                }
            }
            box.TextChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            // 색 클릭 시 팔레트 팝업 → 스와치 선택 시 hex 반영
            var palette = new WrapPanel { Width = 8 * 26 };
            var popup = new Popup
            {
                StaysOpen = false,
                AllowsTransparency = true,
                Placement = PlacementMode.Bottom,
                PlacementTarget = preview,
            };
            foreach (var hex in PaletteColors)
            {
                var captured = hex;
                var swatch = new Button
                {
                    Width = 24, Height = 24, Margin = new Thickness(1),
                    Background = BrushFromHex(hex),
                    BorderBrush = Brushes.DarkGray,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = hex,
                };
                swatch.Click += (_, _) => { box.Text = captured; popup.IsOpen = false; };
                palette.Children.Add(swatch);
            }
            popup.Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                Child = palette,
            };
            preview.MouseLeftButtonUp += (_, _) => popup.IsOpen = true;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(box);
            row.Children.Add(preview);
            row.Children.Add(popup);
            return (box, preview, row);
        }

        var textColor = MakeColorRow("원문 색", settings.TextColor);
        var karaokeColor = MakeColorRow("노래방 진행 색", settings.KaraokeColor);
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
            Content = "글자 단위 노래방 (지원 곡: Kugou/QQ 등)",
            IsChecked = settings.CharacterKaraoke,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var hideSameTransCheck = new CheckBox
        {
            Content = "원문과 같은 번역 숨기기",
            IsChecked = settings.HideSameTranslation,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var fadeCheck = new CheckBox
        {
            Content = "가사 나타남/사라짐 페이드 효과",
            IsChecked = settings.FadeAnimation,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var hideOnHoverCheck = new CheckBox
        {
            Content = "마우스를 올리면 오버레이 숨기기(가림 방지)",
            IsChecked = settings.HideOnMouseOver,
            Margin = new Thickness(0, 6, 0, 0),
        };

        // ---- 오버레이 배경 ----
        var bgEnableCheck = new CheckBox
        {
            Content = "오버레이 배경 표시",
            IsChecked = settings.OverlayBackgroundEnabled,
            Margin = new Thickness(0, 12, 0, 4),
        };
        var bgColor = MakeColorRow("배경 색", settings.OverlayBackgroundColor);
        var bgOpacityLabel = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        var bgOpacitySlider = new Slider
        {
            Minimum = 0, Maximum = 1, Value = Math.Clamp(settings.OverlayBackgroundOpacity, 0, 1),
            TickFrequency = 0.05, IsSnapToTickEnabled = true,
        };
        void UpdateBgOpacityLabel() => bgOpacityLabel.Text = $"배경 불투명도: {bgOpacitySlider.Value * 100:0}%";
        bgOpacitySlider.ValueChanged += (_, _) => UpdateBgOpacityLabel();
        UpdateBgOpacityLabel();

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
            settings.HideSameTranslation = hideSameTransCheck.IsChecked == true;
            settings.FadeAnimation = fadeCheck.IsChecked == true;
            settings.HideOnMouseOver = hideOnHoverCheck.IsChecked == true;
            settings.OverlayBackgroundEnabled = bgEnableCheck.IsChecked == true;
            settings.OverlayBackgroundColor = NormalizeHex(bgColor.Box.Text, settings.OverlayBackgroundColor);
            settings.OverlayBackgroundOpacity = bgOpacitySlider.Value;
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
        panel.Children.Add(hideSameTransCheck);
        panel.Children.Add(fadeCheck);
        panel.Children.Add(hideOnHoverCheck);
        panel.Children.Add(bgEnableCheck);
        panel.Children.Add(bgColor.Row);
        panel.Children.Add(bgOpacityLabel);
        panel.Children.Add(bgOpacitySlider);
        panel.Children.Add(saveButton);
        Content = panel;
    }
}
