using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LyricsX.App.Overlay;

/// <summary>
/// 외곽선 + 그림자 + 카라오케 진행 채움 텍스트 요소 (Spike.Overlay에서 승격).
/// FormattedText.BuildGeometry로 글리프 지오메트리를 얻어
/// (1) 외곽선 → (2) 기본색 → (3) 진행 클립 강조색 순으로 그린다.
/// </summary>
public sealed class OutlinedTextElement : FrameworkElement
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedTextElement),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((OutlinedTextElement)d)._formatted = null));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(OutlinedTextElement),
            new FrameworkPropertyMetadata(32.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((OutlinedTextElement)d)._formatted = null));

    public static readonly DependencyProperty KaraokeProgressProperty =
        DependencyProperty.Register(nameof(KaraokeProgress), typeof(double), typeof(OutlinedTextElement),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>0~1. 현재 라인 진행 비율 (왼쪽부터 강조색 채움)</summary>
    public double KaraokeProgress
    {
        get => (double)GetValue(KaraokeProgressProperty);
        set => SetValue(KaraokeProgressProperty, value);
    }

    public Brush Fill { get; set; } = Brushes.White;
    public Brush? KaraokeFill { get; set; }
    public Brush Stroke { get; set; } = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x00, 0x00));
    public double StrokeThickness { get; set; } = 3.0;
    public FontFamily FontFamily { get; set; } = new("Segoe UI");

    private FormattedText? _formatted;

    public OutlinedTextElement()
    {
        IsHitTestVisible = false;
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 6,
            ShadowDepth = 1.5,
            Opacity = 0.85,
        };
    }

    private FormattedText BuildText() => _formatted ??= new FormattedText(
        Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
        FontSize,
        Fill,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text)) return new Size(0, 0);
        var ft = BuildText();
        return new Size(ft.WidthIncludingTrailingWhitespace + StrokeThickness * 2, ft.Height + StrokeThickness * 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text)) return;
        var ft = BuildText();
        var origin = new Point(StrokeThickness, StrokeThickness);
        var geometry = ft.BuildGeometry(origin);
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };

        dc.DrawGeometry(null, pen, geometry);
        dc.DrawGeometry(Fill, null, geometry);

        if (KaraokeFill is not null && KaraokeProgress > 0)
        {
            var width = (ft.WidthIncludingTrailingWhitespace + StrokeThickness * 2) * Math.Clamp(KaraokeProgress, 0.0, 1.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, Math.Max(RenderSize.Height, ft.Height + StrokeThickness * 2))));
            dc.DrawGeometry(KaraokeFill, null, geometry);
            dc.Pop();
        }
    }
}
