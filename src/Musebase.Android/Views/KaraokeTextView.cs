using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Musebase.Core;
// System.IO.Path와의 모호 참조(CS0104) 방지 — 이 파일의 Path는 그래픽 Path.
using Path = Android.Graphics.Path;
// View.Layout(int,int,int,int) 메서드가 Android.Text.Layout 타입명을 가리므로 별칭.
using TextLayout = Android.Text.Layout;

namespace Musebase.Android.Views;

/// <summary>
/// 글자 단위 카라오케 채움을 그리는 커스텀 뷰. Windows 오버레이(OutlinedTextElement)의
/// "텍스트를 두 번 그린다 — 베이스(흰색) 위에 채움색(노랑)을 진행 위치까지 클립" 개념을 포팅.
/// (코드 복사 아님 — 클립-리드로우 아이디어만 차용.)
///
/// 채움 위치 계산:
/// - 글자 타임태그(<see cref="InlineTimeTags"/>)가 있으면 <see cref="InlineTimeTags.CharIndexAt"/>로
///   경과 시간에 해당하는 (소수) 글자 인덱스를 얻어 그 지점까지 채운다.
/// - 없으면 라인 표시 구간 대비 경과 비율을 글자 수에 곱해 근사한다(라인 단위 폴백).
///
/// 부드러운 채움: 엔진의 <c>LineProgressChanged</c>는 100ms마다만 온다. 매 갱신마다 앵커
/// (기준 경과 + 실시간)를 기록하고, 재생 중에는 <see cref="View.PostInvalidateOnAnimation()"/>로
/// 프레임마다 앵커+경과로 보간해 60fps로 채운다(Windows의 로컬 보간과 동일한 접근).
///
/// 멀티라인 대응: <see cref="StaticLayout"/>으로 줄바꿈하고, 채움은 완성된 윗줄 전체 + 현재 줄의
/// 좌측부터 채움 글자 x까지를 Path 클립으로 칠한다(가운데 정렬·소수 글자 위치 보정 포함).
/// </summary>
public sealed class KaraokeTextView : View
{
    private static readonly Color BaseColor = Color.White;
    // Windows 기본 KaraokeColor와 통일(#FFEB3B).
    private static readonly Color FillColor = Color.Argb(0xFF, 0xFF, 0xEB, 0x3B);

    private readonly TextPaint _paint;
    private readonly int _maxWidthPx;

    private string _text = "";
    private InlineTimeTags? _karaoke;
    private double _lineSpanSeconds;

    private StaticLayout? _layout;
    private string? _builtText;
    private int _builtWidth = -1;

    // 보간 앵커: _elapsedBase는 마지막 갱신 시점의 라인 경과(초), _anchorMs는 그때의 시계.
    private double _elapsedBase;
    private long _anchorMs;
    private bool _animating;

    public KaraokeTextView(Context context, float textSizeSp, int maxWidthPx) : base(context)
    {
        _maxWidthPx = maxWidthPx;
        var metrics = context.Resources!.DisplayMetrics!;
        _paint = new TextPaint(PaintFlags.AntiAlias | PaintFlags.SubpixelText)
        {
            Color = BaseColor,
            // ScaledDensity(API 34 deprecated) 대신 비-폐기 API로 sp→px 변환.
            TextSize = TypedValue.ApplyDimension(ComplexUnitType.Sp, textSizeSp, metrics),
        };
        _paint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        // 앱 위에 떠 있어도 읽히도록 얇은 그림자.
        _paint.SetShadowLayer(6f, 0f, 2f, Color.Argb(0xC8, 0, 0, 0));
    }

    /// <summary>새 라인 설정(원문 + 글자 타임태그 + 라인 표시 구간). 경과는 0으로 리셋.</summary>
    public void SetLine(string? text, InlineTimeTags? karaoke, double lineSpanSeconds)
    {
        _text = text ?? "";
        _karaoke = karaoke;
        _lineSpanSeconds = lineSpanSeconds;
        _elapsedBase = 0;
        _anchorMs = SystemClock.UptimeMillis();
        _layout = null; // 다음 측정에서 재구성
        RequestLayout();
        Invalidate();
    }

    /// <summary>라인 시작 이후 경과(초) 갱신 — 앵커를 재설정해 이후 프레임을 보간한다.</summary>
    public void SetElapsed(double seconds)
    {
        _elapsedBase = seconds;
        _anchorMs = SystemClock.UptimeMillis();
        Invalidate();
    }

    /// <summary>재생 중이면 프레임 보간을 돌리고, 일시정지면 현재 채움에서 고정한다.</summary>
    public void SetAnimating(bool animating)
    {
        if (_animating == animating) return;
        _animating = animating;
        if (animating)
        {
            _anchorMs = SystemClock.UptimeMillis(); // 정지 동안 흐른 시간 무시
            PostInvalidateOnAnimation();
        }
    }

    private double EffectiveElapsed()
    {
        if (!_animating) return _elapsedBase;
        return _elapsedBase + (SystemClock.UptimeMillis() - _anchorMs) / 1000.0;
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var availMode = MeasureSpec.GetMode(widthMeasureSpec);
        var availSize = MeasureSpec.GetSize(widthMeasureSpec);
        var avail = availMode == MeasureSpecMode.Unspecified ? _maxWidthPx : Math.Min(availSize, _maxWidthPx);
        if (avail < 1) avail = 1;

        var layoutText = _text.Length == 0 ? " " : _text;
        var desired = (int)Math.Ceiling(TextLayout.GetDesiredWidth(layoutText, _paint));
        var width = availMode == MeasureSpecMode.Exactly ? avail : Math.Min(avail, Math.Max(1, desired));

        BuildLayout(layoutText, width);
        SetMeasuredDimension(width, _layout?.Height ?? 0);
    }

    private void BuildLayout(string layoutText, int width)
    {
        if (_layout is not null && _builtText == layoutText && _builtWidth == width) return;
        using var src = new Java.Lang.String(layoutText);
        _layout = StaticLayout.Builder
            .Obtain(src, 0, layoutText.Length, _paint, width)!
            .SetAlignment(TextLayout.Alignment.AlignCenter!)!
            .SetIncludePad(false)!
            .Build();
        _builtText = layoutText;
        _builtWidth = width;
    }

    protected override void OnDraw(Canvas canvas)
    {
        var layout = _layout;
        if (layout is null) return;

        // 1) 베이스(흰색) 전체.
        _paint.Color = BaseColor;
        layout.Draw(canvas);

        // 2) 채움(노랑)을 진행 글자 위치까지 클립해 덧그린다.
        var fillIndex = FillCharIndex();
        if (fillIndex > 0.0 && _text.Length > 0)
        {
            var clip = BuildFillClip(layout, fillIndex);
            if (clip is not null)
            {
                canvas.Save();
                canvas.ClipPath(clip);
                _paint.Color = FillColor;
                layout.Draw(canvas);
                _paint.Color = BaseColor;
                canvas.Restore();
                clip.Dispose();
            }
        }

        if (_animating) PostInvalidateOnAnimation();
    }

    /// <summary>경과 시간 → 채울 (소수) 글자 인덱스. 태그 우선, 없으면 라인 비율 폴백.</summary>
    private double FillCharIndex()
    {
        var elapsed = EffectiveElapsed();
        if (_karaoke is { } tags)
            return Math.Clamp(tags.CharIndexAt(elapsed), 0, _text.Length);
        if (_lineSpanSeconds > 0)
            return _text.Length * Math.Clamp(elapsed / _lineSpanSeconds, 0.0, 1.0);
        return 0;
    }

    /// <summary>
    /// 완성된 윗줄 전체 + 현재 줄 좌측~채움 x 까지를 덮는 클립 Path.
    /// 소수 글자 위치는 다음 글자와 선형 보간해 부드럽게 전진시킨다.
    /// </summary>
    private Path? BuildFillClip(TextLayout layout, double fillIndex)
    {
        var full = (int)Math.Floor(fillIndex);
        var frac = fillIndex - full;
        full = Math.Clamp(full, 0, _text.Length);

        var line = layout.GetLineForOffset(full);
        var x = layout.GetPrimaryHorizontal(full);
        if (frac > 0 && full < _text.Length && layout.GetLineForOffset(full + 1) == line)
        {
            var xNext = layout.GetPrimaryHorizontal(full + 1);
            x += (float)((xNext - x) * frac);
        }

        var lineTop = layout.GetLineTop(line);
        var lineBottom = layout.GetLineBottom(line);
        var lineLeft = layout.GetLineLeft(line);

        var path = new Path();
        if (lineTop > 0)
            path.AddRect(0, 0, Width, lineTop, Path.Direction.Cw!); // 위쪽 완성 줄 전체
        if (x > lineLeft)
            path.AddRect(lineLeft, lineTop, x, lineBottom, Path.Direction.Cw!); // 현재 줄 좌측~채움 x
        return path;
    }
}
