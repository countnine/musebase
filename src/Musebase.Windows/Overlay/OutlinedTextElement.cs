using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Musebase.Core;

namespace Musebase.Windows.Overlay;

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
                (d, _) => ((OutlinedTextElement)d).InvalidateTextCache()));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(OutlinedTextElement),
            new FrameworkPropertyMetadata(32.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((OutlinedTextElement)d).InvalidateTextCache()));

    public static readonly DependencyProperty KaraokeProgressProperty =
        DependencyProperty.Register(nameof(KaraokeProgress), typeof(double), typeof(OutlinedTextElement),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>글자 단위 카라오케 시각(초, 라인 시작 기준). InlineKaraoke가 있을 때 사용.</summary>
    public static readonly DependencyProperty KaraokeTimeProperty =
        DependencyProperty.Register(nameof(KaraokeTime), typeof(double), typeof(OutlinedTextElement),
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

    /// <summary>0~1. 현재 라인 진행 비율 (왼쪽부터 강조색 채움). InlineKaraoke 없을 때 폴백.</summary>
    public double KaraokeProgress
    {
        get => (double)GetValue(KaraokeProgressProperty);
        set => SetValue(KaraokeProgressProperty, value);
    }

    /// <summary>라인 시작 기준 경과 시각(초). InlineKaraoke가 설정된 경우 글자 위치 계산에 쓰인다.</summary>
    public double KaraokeTime
    {
        get => (double)GetValue(KaraokeTimeProperty);
        set => SetValue(KaraokeTimeProperty, value);
    }

    /// <summary>글자 단위 타임태그. 설정 시 KaraokeTime으로 글자 위치까지 채운다(없으면 KaraokeProgress 폴백).</summary>
    public InlineTimeTags? InlineKaraoke
    {
        get => _inlineKaraoke;
        set { _inlineKaraoke = value; InvalidateVisual(); }
    }

    public Brush Fill { get; set; } = Brushes.White;
    public Brush? KaraokeFill { get; set; }
    public Brush Stroke { get; set; } = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x00, 0x00));
    public double StrokeThickness { get; set; } = 3.0;
    public FontFamily FontFamily { get; set; } = new("Segoe UI");

    private FormattedText? _formatted;
    private InlineTimeTags? _inlineKaraoke;

    // 글자별 누적 x 오프셋(글리프 우측 끝) 캐시 — 텍스트/폰트/외곽선 변경 시 무효화
    private double[]? _charX;
    private double _charXStroke = -1;

    private void InvalidateTextCache()
    {
        _formatted = null;
        _charX = null;
    }

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

        var fillWidth = KaraokeFillWidth(ft, origin);
        if (KaraokeFill is not null && fillWidth > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, fillWidth, Math.Max(RenderSize.Height, ft.Height + StrokeThickness * 2))));
            dc.DrawGeometry(KaraokeFill, null, geometry);
            dc.Pop();
        }
    }

    /// <summary>채울 픽셀 폭: 인라인 태그가 있으면 글자 위치까지, 없으면 라인 비율 폴백.</summary>
    private double KaraokeFillWidth(FormattedText ft, Point origin)
    {
        if (_inlineKaraoke is { } tags && Text.Length > 0)
        {
            var charPos = Math.Clamp(tags.CharIndexAt(KaraokeTime), 0, Text.Length);
            return CharXOffset(ft, origin, charPos);
        }
        if (KaraokeProgress > 0)
            return (ft.WidthIncludingTrailingWhitespace + StrokeThickness * 2) * Math.Clamp(KaraokeProgress, 0.0, 1.0);
        return 0;
    }

    /// <summary>소수 글자 위치의 픽셀 x(요소 좌표계). 글자별 누적 폭을 캐시해 보간한다.</summary>
    private double CharXOffset(FormattedText ft, Point origin, double charPos)
    {
        var len = Text.Length;
        if (_charX is null || _charX.Length != len + 1 || _charXStroke != StrokeThickness)
        {
            _charX = new double[len + 1];
            _charX[0] = origin.X;
            for (var i = 1; i <= len; i++)
            {
                var geo = ft.BuildHighlightGeometry(origin, 0, i);
                _charX[i] = geo is not null && !geo.IsEmpty() ? geo.Bounds.Right : _charX[i - 1];
            }
            _charXStroke = StrokeThickness;
        }

        var lo = (int)Math.Floor(charPos);
        if (lo >= len) return _charX[len];
        if (lo < 0) return _charX[0];
        return _charX[lo] + (_charX[lo + 1] - _charX[lo]) * (charPos - lo);
    }
}
