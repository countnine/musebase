using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Musebase.Windows.Services;
using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Windows;

/// <summary>
/// 설정 창. 탭으로 구성: [일반] 언어·번역, [오버레이 스타일] 색·배경·표시 옵션.
/// UI 문자열은 <see cref="Loc"/>로 현지화하며, 언어 변경 시 내용을 즉시 다시 그린다.
/// 긴 번역 문자열이 잘리지 않도록 헤더·체크박스·라벨은 줄바꿈한다.
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

    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private readonly Action? _onCheckUpdates;
    private bool _rebuilding; // 언어 변경으로 콤보를 프로그램적으로 세팅할 때 이벤트 억제

    private static SolidColorBrush BrushFromHex(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.Transparent); }
    }

    public SettingsWindow(AppSettings settings, Action onSaved, Action? onCheckUpdates = null)
    {
        _settings = settings;
        _onSaved = onSaved;
        _onCheckUpdates = onCheckUpdates;

        Width = 470;
        SizeToContent = SizeToContent.Height; // 내용 높이에 맞춰 자동(세로 스크롤 없음)
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        MaxHeight = SystemParameters.WorkArea.Height; // 화면을 넘지 않도록 상한

        BuildUi();
        Loc.CultureChanged += BuildUi;                 // 언어 바뀌면 다시 그림
        Closed += (_, _) => Loc.CultureChanged -= BuildUi;
    }

    /// <summary>언어 선택 항목(코드 + 표시명). "system"은 시스템 언어를 따른다.</summary>
    private sealed record LanguageChoice(string Code, string Display);

    /// <summary>번역 엔진 선택 항목(id + 표시명).</summary>
    private sealed record EngineChoice(string Id, string Display);

    // 줄바꿈되는 섹션 헤더
    private static TextBlock Header(string key) => new()
    {
        Text = Loc.T(key),
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 2),
    };

    // 내용이 길면 줄바꿈되는 체크박스
    private static CheckBox WrapCheck(string key, bool isChecked, Thickness margin) => new()
    {
        Content = new TextBlock { Text = Loc.T(key), TextWrapping = TextWrapping.Wrap },
        IsChecked = isChecked,
        Margin = margin,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };

    private void BuildUi()
    {
        _rebuilding = true;
        try
        {
            Title = Loc.T("settings.title");
            var settings = _settings;

            // ================= 탭 패널 =================
            // 폭을 명시(SizeToContent.Height에서 폭 전파 누락으로 글자단위 줄바꿈되는 문제 방지)
            var general = new StackPanel { Margin = new Thickness(16), Width = 400, HorizontalAlignment = HorizontalAlignment.Left };
            var translation = new StackPanel { Margin = new Thickness(16), Width = 400, HorizontalAlignment = HorizontalAlignment.Left };

            // 표시 언어
            var langChoices = new List<LanguageChoice> { new(Loc.SystemSetting, Loc.T("settings.language.system")) };
            langChoices.AddRange(Loc.SupportedLanguages.Select(l => new LanguageChoice(l.Code, l.NativeName)));
            var uiLangBox = new ComboBox
            {
                ItemsSource = langChoices,
                DisplayMemberPath = nameof(LanguageChoice.Display),
                SelectedValuePath = nameof(LanguageChoice.Code),
                SelectedValue = Loc.Setting,
                Width = 210,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 6),
            };
            if (uiLangBox.SelectedValue is null) uiLangBox.SelectedIndex = 0;
            uiLangBox.SelectionChanged += (_, _) =>
            {
                if (_rebuilding || uiLangBox.SelectedValue is not string code || code == Loc.Setting) return;
                settings.UiLanguage = code;
                settings.Save();
                Loc.SetLanguage(code); // → CultureChanged → BuildUi 재실행
            };

            var contribute = new TextBlock { Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            var link = new Hyperlink(new Run(Loc.T("settings.contribute")))
            {
                NavigateUri = new Uri(Loc.ContributionUrl),
                ToolTip = Loc.T("settings.contribute.tooltip"),
            };
            link.RequestNavigate += OnRequestNavigate;
            contribute.Inlines.Add(link);

            // API 키: 기본은 마스킹(PasswordBox), 눈(👁) 토글로만 잠깐 평문 표시.
            var keyMasked = new PasswordBox { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 4, 10) };
            keyMasked.Password = settings.DeeplApiKey ?? "";
            var keyPlain = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 4, 10), Visibility = Visibility.Collapsed };
            var keyReveal = new ToggleButton
            {
                Content = "👁", Width = 30,
                Margin = new Thickness(0, 2, 10, 10),
                ToolTip = Loc.T("settings.deepl.key.reveal"),
            };
            keyReveal.Checked += (_, _) =>
            {
                keyPlain.Text = keyMasked.Password;
                keyMasked.Visibility = Visibility.Collapsed;
                keyPlain.Visibility = Visibility.Visible;
                keyPlain.Focus();
            };
            keyReveal.Unchecked += (_, _) =>
            {
                keyMasked.Password = keyPlain.Text;
                keyPlain.Visibility = Visibility.Collapsed;
                keyMasked.Visibility = Visibility.Visible;
            };
            string CurrentKey() => keyReveal.IsChecked == true ? keyPlain.Text : keyMasked.Password;

            var keyRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(keyMasked, 0);
            Grid.SetColumn(keyPlain, 0);
            Grid.SetColumn(keyReveal, 1);
            keyRow.Children.Add(keyMasked);
            keyRow.Children.Add(keyPlain);
            keyRow.Children.Add(keyReveal);

            var langBox = new ComboBox
            {
                IsEditable = true,
                Text = settings.TargetLanguage,
                ItemsSource = CommonLanguages,
                Width = 210,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 10),
            };

            general.Children.Add(Header("settings.language.header"));
            general.Children.Add(uiLangBox);

            // 미니창 닫기(X) 동작: 켜면 트레이로 숨김(작업표시줄에서 사라짐), 꺼짐(기본)=최소화 상주.
            var closeToTrayCheck = WrapCheck("settings.mini.closeToTray", settings.MiniWindowCloseToTray, new Thickness(0, 0, 0, 10));
            general.Children.Add(closeToTrayCheck);

            // ---- [번역] 탭 ----
            translation.Children.Add(Header("settings.deepl.lang.header"));
            translation.Children.Add(langBox);
            translation.Children.Add(Header("settings.deepl.key.header"));
            translation.Children.Add(keyRow);

            // 번역 엔진 선택 (레지스트리) + LibreTranslate 엔드포인트(자체호스팅)
            var engineChoices = new List<EngineChoice> { new("none", Loc.T("settings.translation.none")) };
            engineChoices.AddRange(TranslatorRegistry.All.Select(d => new EngineChoice(d.Id, d.DisplayName)));
            var engineBox = new ComboBox
            {
                ItemsSource = engineChoices,
                DisplayMemberPath = nameof(EngineChoice.Display),
                SelectedValuePath = nameof(EngineChoice.Id),
                SelectedValue = settings.EffectiveTranslationEngine,
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 6),
            };
            if (engineBox.SelectedValue is null) engineBox.SelectedIndex = 0;

            var endpointBox = new TextBox
            {
                Text = settings.LibreTranslateEndpoint ?? "",
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var endpointPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            endpointPanel.Children.Add(Header("settings.translation.libre.endpoint"));
            endpointPanel.Children.Add(endpointBox);
            void UpdateEndpointVisibility() =>
                endpointPanel.Visibility = (engineBox.SelectedValue as string) == "libretranslate"
                    ? Visibility.Visible : Visibility.Collapsed;
            engineBox.SelectionChanged += (_, _) => UpdateEndpointVisibility();
            UpdateEndpointVisibility();

            translation.Children.Add(Header("settings.translation.engine.header"));
            translation.Children.Add(engineBox);
            translation.Children.Add(endpointPanel);

            // DeepL 실패 시 LibreTranslate 공개 서버로 자동 전환 (신규, 기본 꺼짐)
            var fallbackCheck = WrapCheck("settings.translation.fallback", settings.TranslationFallbackToFree, new Thickness(0, 12, 0, 0));
            var fallbackWarn = new TextBlock
            {
                Text = Loc.T("settings.translation.fallback.warn"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 2, 0, 0),
                Visibility = settings.TranslationFallbackToFree ? Visibility.Visible : Visibility.Collapsed,
            };
            fallbackCheck.Checked += (_, _) => fallbackWarn.Visibility = Visibility.Visible;
            fallbackCheck.Unchecked += (_, _) => fallbackWarn.Visibility = Visibility.Collapsed;
            translation.Children.Add(fallbackCheck);
            translation.Children.Add(fallbackWarn);

            // 번역 표시 정책: 대상 언어 번역만 표시(제공자의 다른 언어 번역 숨김) + 원문과 같은 번역 숨김
            var onlyTargetTransCheck = WrapCheck("settings.onlyTargetTranslation", settings.ShowOnlyTargetTranslation, new Thickness(0, 12, 0, 0));
            var hideSameTransCheck = WrapCheck("settings.hideSameTranslation", settings.HideSameTranslation, new Thickness(0, 6, 0, 0));
            translation.Children.Add(onlyTargetTransCheck);
            translation.Children.Add(hideSameTransCheck);
            translation.Children.Add(contribute);

            // 오버레이 동작 토글(오버레이 스타일 탭으로 이관)
            var karaokeCheck = WrapCheck("settings.karaoke.check", settings.CharacterKaraoke, new Thickness(0, 14, 0, 0));
            var fadeCheck = WrapCheck("settings.fade", settings.FadeAnimation, new Thickness(0, 6, 0, 0));
            var hideOnHoverCheck = WrapCheck("settings.hideOnMouseOver", settings.HideOnMouseOver, new Thickness(0, 6, 0, 0));

            // 가사 소스 선택 (레지스트리). 공식/비공식 표시 — 비공식은 공개 배포 리스크 안내.
            general.Children.Add(Header("settings.sources.header"));
            general.Children.Add(new TextBlock
            {
                Text = Loc.T("settings.sources.hint"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var sourceChecks = new List<(string Id, CheckBox Box)>();
            foreach (var d in LyricsSourceRegistry.All)
            {
                var tag = Loc.T(d.IsOfficialApi ? "settings.sources.official" : "settings.sources.unofficial");
                var cb = new CheckBox
                {
                    Content = new TextBlock { Text = $"{d.DisplayName} — {tag}", TextWrapping = TextWrapping.Wrap },
                    IsChecked = settings.EnabledLyricsSources.Contains(d.Id, StringComparer.OrdinalIgnoreCase),
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                sourceChecks.Add((d.Id, cb));
                general.Children.Add(cb);
            }
            general.Children.Add(new TextBlock
            {
                Text = Loc.T("settings.sources.restart"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            // ---- 브라우저 디스플레이(태블릿/TV) — 켜기는 트레이 메뉴에서, 여기선 포트·LAN 설정 ----
            general.Children.Add(Header("settings.browserDisplay.header"));
            general.Children.Add(new TextBlock
            {
                Text = Loc.T("settings.browserDisplay.hint"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
            var browserPortBox = new TextBox
            {
                Text = settings.BrowserDisplayPort.ToString(CultureInfo.InvariantCulture),
                Width = 90,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var browserPortRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            browserPortRow.Children.Add(new TextBlock
            {
                Text = Loc.T("settings.browserDisplay.port"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            browserPortRow.Children.Add(browserPortBox);
            general.Children.Add(browserPortRow);
            var browserLanCheck = WrapCheck("settings.browserDisplay.lan", settings.BrowserDisplayLan, new Thickness(0, 8, 0, 0));
            general.Children.Add(browserLanCheck);
            general.Children.Add(new TextBlock
            {
                Text = Loc.T("settings.browserDisplay.lan.hint"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 2, 0, 0),
            });

            // 사용 통계(텔레메트리, ADR-0004) — 토글 즉시 반영(저장 버튼과 무관)
            general.Children.Add(Header("settings.telemetry.header"));
            var telemetryBasicCheck = WrapCheck("telemetry.consent.basic", settings.TelemetryBasicEnabled, new Thickness(0, 2, 0, 0));
            var telemetryQualityCheck = WrapCheck("telemetry.consent.quality", settings.TelemetryQualityEnabled, new Thickness(0, 6, 0, 0));
            void ApplyTelemetryConsent()
            {
                if (_rebuilding) return;
                settings.TelemetryBasicEnabled = telemetryBasicCheck.IsChecked == true;
                settings.TelemetryQualityEnabled = telemetryQualityCheck.IsChecked == true;
                if (settings.TelemetryBasicEnabled || settings.TelemetryQualityEnabled)
                    TelemetryClient.EnsureClientId(settings); // 최초 켜짐 시 익명 GUID 생성(+저장)
                settings.Save();
            }
            telemetryBasicCheck.Checked += (_, _) => ApplyTelemetryConsent();
            telemetryBasicCheck.Unchecked += (_, _) => ApplyTelemetryConsent();
            telemetryQualityCheck.Checked += (_, _) => ApplyTelemetryConsent();
            telemetryQualityCheck.Unchecked += (_, _) => ApplyTelemetryConsent();
            general.Children.Add(telemetryBasicCheck);
            general.Children.Add(telemetryQualityCheck);

            var telemetryDoc = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            var telemetryLink = new Hyperlink(new Run(Loc.T("settings.telemetry.details")))
            {
                NavigateUri = new Uri(TelemetryConsentWindow.TelemetryDocUrl),
            };
            telemetryLink.RequestNavigate += OnRequestNavigate;
            telemetryDoc.Inlines.Add(telemetryLink);
            general.Children.Add(telemetryDoc);

            var resetIdButton = new Button
            {
                Content = Loc.T("settings.telemetry.resetId"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 6, 0, 0),
            };
            resetIdButton.Click += (_, _) =>
            {
                TelemetryClient.ResetClientId(settings);
                MessageBox.Show(this, Loc.T("settings.telemetry.resetId.done"),
                    Loc.T("settings.telemetry.header"), MessageBoxButton.OK, MessageBoxImage.Information);
            };
            general.Children.Add(resetIdButton);

            // ================= [오버레이 스타일] 탭 =================
            var appearance = new StackPanel { Margin = new Thickness(16), Width = 400, HorizontalAlignment = HorizontalAlignment.Left };

            appearance.Children.Add(new TextBlock
            {
                Text = Loc.T("settings.fontHint"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

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
                    ToolTip = Loc.T("settings.color.pick.tooltip"),
                };
                void UpdatePreview()
                {
                    try { preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(box.Text.Trim())); }
                    catch { preview.Background = Brushes.Transparent; }
                }
                box.TextChanged += (_, _) => UpdatePreview();
                UpdatePreview();

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
                row.Children.Add(new TextBlock
                {
                    Text = label, Width = 120, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(box);
                row.Children.Add(preview);
                row.Children.Add(popup);
                return (box, preview, row);
            }

            var textColor = MakeColorRow(Loc.T("settings.color.text"), settings.TextColor);
            var karaokeColor = MakeColorRow(Loc.T("settings.color.karaoke"), settings.KaraokeColor);
            var translationColor = MakeColorRow(Loc.T("settings.color.translation"), settings.TranslationColor);
            var outlineColor = MakeColorRow(Loc.T("settings.color.outline"), settings.OutlineColor);

            var outlineLabel = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            var outlineSlider = new Slider
            {
                Minimum = 0, Maximum = 8, Value = settings.OutlineThickness,
                TickFrequency = 0.5, IsSnapToTickEnabled = true,
                Width = 210, HorizontalAlignment = HorizontalAlignment.Left,
            };
            void UpdateOutlineLabel() =>
                outlineLabel.Text = Loc.T("settings.outline.thickness", ("value", outlineSlider.Value.ToString("0.#", CultureInfo.CurrentCulture)));
            outlineSlider.ValueChanged += (_, _) => UpdateOutlineLabel();
            UpdateOutlineLabel();

            // 배경
            var bgEnableCheck = WrapCheck("settings.background.show", settings.OverlayBackgroundEnabled, new Thickness(0, 12, 0, 4));
            var bgColor = MakeColorRow(Loc.T("settings.color.background"), settings.OverlayBackgroundColor);
            var bgOpacityLabel = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            var bgOpacitySlider = new Slider
            {
                Minimum = 0, Maximum = 1, Value = Math.Clamp(settings.OverlayBackgroundOpacity, 0, 1),
                TickFrequency = 0.05, IsSnapToTickEnabled = true,
                Width = 210, HorizontalAlignment = HorizontalAlignment.Left,
            };
            void UpdateBgOpacityLabel() =>
                bgOpacityLabel.Text = Loc.T("settings.background.opacity", ("value", (bgOpacitySlider.Value * 100).ToString("0", CultureInfo.CurrentCulture)));
            bgOpacitySlider.ValueChanged += (_, _) => UpdateBgOpacityLabel();
            UpdateBgOpacityLabel();

            appearance.Children.Add(textColor.Row);
            appearance.Children.Add(karaokeColor.Row);
            appearance.Children.Add(translationColor.Row);
            appearance.Children.Add(outlineColor.Row);
            appearance.Children.Add(outlineLabel);
            appearance.Children.Add(outlineSlider);
            appearance.Children.Add(bgEnableCheck);
            appearance.Children.Add(bgColor.Row);
            appearance.Children.Add(bgOpacityLabel);
            appearance.Children.Add(bgOpacitySlider);
            appearance.Children.Add(karaokeCheck);
            appearance.Children.Add(fadeCheck);
            appearance.Children.Add(hideOnHoverCheck);

            // ================= [정보] 탭 =================
            var about = BuildAboutTab();

            // ================= 탭 컨트롤 =================
            // ScrollViewer 없이 각 탭 내용을 직접 넣고, 창을 SizeToContent로 맞춰 세로 스크롤이 없게 한다.
            var tabs = new TabControl { Margin = new Thickness(8, 8, 8, 0) };
            tabs.Items.Add(new TabItem { Header = Loc.T("settings.tab.general"), Content = general });
            tabs.Items.Add(new TabItem { Header = Loc.T("settings.tab.translation"), Content = translation });
            tabs.Items.Add(new TabItem { Header = Loc.T("settings.tab.appearance"), Content = appearance });
            tabs.Items.Add(new TabItem { Header = Loc.T("settings.tab.about"), Content = about });
            // 탭 전환 시 새 탭 높이에 맞춰 창을 다시 계산(콤보 등 하위 SelectionChanged 버블은 제외)
            tabs.SelectionChanged += (_, e) =>
            {
                if (e.OriginalSource is TabControl)
                {
                    SizeToContent = SizeToContent.Manual;
                    SizeToContent = SizeToContent.Height;
                }
            };

            // ================= 저장 버튼 =================
            static string NormalizeHex(string input, string fallback)
            {
                var t = input.Trim();
                try { _ = ColorConverter.ConvertFromString(t); return t; }
                catch { return fallback; }
            }
            var saveButton = new Button
            {
                Content = Loc.T("common.save"),
                Width = 90,
                IsDefault = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 10, 16, 12),
            };
            saveButton.Click += (_, _) =>
            {
                var enteredKey = CurrentKey();
                settings.DeeplApiKey = string.IsNullOrWhiteSpace(enteredKey) ? null : enteredKey.Trim();
                settings.TargetLanguage = string.IsNullOrWhiteSpace(langBox.Text) ? AppSettings.DefaultTargetLanguage() : langBox.Text.Trim().ToUpperInvariant();
                settings.TranslationEngine = engineBox.SelectedValue as string;
                settings.LibreTranslateEndpoint = string.IsNullOrWhiteSpace(endpointBox.Text) ? null : endpointBox.Text.Trim();
                settings.TranslationFallbackToFree = fallbackCheck.IsChecked == true;
                settings.EnabledLyricsSources = sourceChecks.Where(s => s.Box.IsChecked == true).Select(s => s.Id).ToList();
                settings.MiniWindowCloseToTray = closeToTrayCheck.IsChecked == true;
                // 브라우저 디스플레이 포트/LAN (실행 중 변경은 다음 시작부터 적용 — 토글 재시작)
                if (int.TryParse(browserPortBox.Text.Trim(), out var browserPort) && browserPort is >= 0 and <= 65535)
                    settings.BrowserDisplayPort = browserPort;
                settings.BrowserDisplayLan = browserLanCheck.IsChecked == true;
                settings.TextColor = NormalizeHex(textColor.Box.Text, settings.TextColor);
                settings.KaraokeColor = NormalizeHex(karaokeColor.Box.Text, settings.KaraokeColor);
                settings.TranslationColor = NormalizeHex(translationColor.Box.Text, settings.TranslationColor);
                settings.OutlineColor = NormalizeHex(outlineColor.Box.Text, settings.OutlineColor);
                settings.OutlineThickness = outlineSlider.Value;
                settings.CharacterKaraoke = karaokeCheck.IsChecked == true;
                settings.HideSameTranslation = hideSameTransCheck.IsChecked == true;
                settings.ShowOnlyTargetTranslation = onlyTargetTransCheck.IsChecked == true;
                settings.FadeAnimation = fadeCheck.IsChecked == true;
                settings.HideOnMouseOver = hideOnHoverCheck.IsChecked == true;
                settings.OverlayBackgroundEnabled = bgEnableCheck.IsChecked == true;
                settings.OverlayBackgroundColor = NormalizeHex(bgColor.Box.Text, settings.OverlayBackgroundColor);
                settings.OverlayBackgroundOpacity = bgOpacitySlider.Value;
                settings.Save();
                _onSaved();
                Close();
            };

            var root = new DockPanel();
            DockPanel.SetDock(saveButton, Dock.Bottom);
            root.Children.Add(saveButton);
            root.Children.Add(tabs);
            Content = root;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>[정보](About) 탭: 앱 이름·버전·출처·라이선스·링크·업데이트 확인.</summary>
    private StackPanel BuildAboutTab()
    {
        var about = new StackPanel { Margin = new Thickness(16), Width = 400, HorizontalAlignment = HorizontalAlignment.Left };

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var versionText = $"{v.Major}.{v.Minor}.{v.Build}";

        about.Children.Add(new TextBlock
        {
            Text = Loc.T("about.appName"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        });
        about.Children.Add(new TextBlock
        {
            Text = Loc.T("about.version", ("version", versionText)),
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = Loc.T("about.formerly"),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = Loc.T("about.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        // 라이선스 + 원작 출처
        about.Children.Add(new TextBlock
        {
            Text = Loc.T("about.license"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = Loc.T("about.attribution"),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        // 링크 3개
        TextBlock LinkRow(string labelKey, string url)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            var hl = new Hyperlink(new Run(Loc.T(labelKey))) { NavigateUri = new Uri(url) };
            hl.RequestNavigate += OnRequestNavigate;
            tb.Inlines.Add(hl);
            return tb;
        }
        about.Children.Add(new TextBlock { Height = 6 });
        about.Children.Add(LinkRow("about.link.github", "https://github.com/countnine/musebase"));
        about.Children.Add(LinkRow("about.link.home", "https://countnine.github.io/musebase-home/"));
        about.Children.Add(LinkRow("about.link.license", "https://github.com/countnine/musebase/blob/master/LICENSE"));

        // 업데이트 확인 버튼 — 트레이 로직 재사용(주입된 경우에만 표시)
        if (_onCheckUpdates is { } check)
        {
            var updateButton = new Button
            {
                Content = Loc.T("about.checkUpdates"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 14, 0, 0),
            };
            updateButton.Click += (_, _) => check();
            about.Children.Add(updateButton);
        }

        return about;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
        catch { /* 브라우저 실행 실패는 무시 */ }
        e.Handled = true;
    }
}
