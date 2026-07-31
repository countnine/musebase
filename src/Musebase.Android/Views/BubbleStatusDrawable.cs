using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;

namespace Musebase.Android.Views;

/// <summary>버블이 알리는 가사 상태.</summary>
public enum BubbleLyricsState
{
    /// <summary>가사를 찾는 중 — 테두리 호가 회전한다.</summary>
    Searching,
    /// <summary>가사가 있다 — 테두리·음표가 밝다.</summary>
    Found,
    /// <summary>못 찾았거나 재생 중인 곡이 없다 — 테두리·음표가 회색.</summary>
    Missing,
}

/// <summary>
/// 버블(플로팅) 배경. 한 원 안에 서로 다른 세 가지를 겹치지 않게 싣는다.
///
/// - **테두리 링** = 가사 상태. 검색 중이면 흐린 트랙 위로 악센트 호가 돌고(플레이스토어의
///   다운로드 진행 링과 같은 감각), 찾았으면 밝은 링, 못 찾았으면 회색 링.
/// - **링의 줄 수** = 오버레이(가사 밴드)가 지금 화면에 보이는지. 보이면 같은 굵기·밝기의 링이
///   안쪽에 하나 더 생겨 두 줄이 된다. 굵기나 밝기로 구분하면 선이 도드라져 화면을 방해한다.
/// - **우상단 점** = 번역 상태 중 알려야 하는 예외(한도·실패=주황, API 꺼짐=회색). 정상이면 점이 없다.
///
/// 안쪽 채움은 상태를 나타내지 **않는다** — 밴드와 같은 재질로 보이도록 오버레이 배경 설정
/// (색·불투명도)을 그대로 따른다. 상태를 채움으로 알리면(예전: 표시 중 흰색으로 채움) 버블이
/// 불투명해져 뒤의 화면을 가린다.
///
/// 회전은 <c>ValueAnimator</c> 대신 핸들러 틱으로 돌린다 — 개발자 옵션에서 애니메이션
/// 배율을 0으로 둔 기기에서도 멈추지 않게 하기 위해서다. 검색 중일 때만 틱이 돈다.
/// </summary>
public sealed class BubbleStatusDrawable : Drawable
{
    /// <summary>링 굵기 — 상태와 무관하게 한 값이다. 굵기로 구분하면 선이 도드라져 화면을 방해한다.</summary>
    private const float RingWidthDp = 2f;
    private const float InnerRingGapDp = 2.6f;    // 보일 때만 추가되는 안쪽 보조 링과의 간격
    private const float SpinSweepDegrees = 96f;   // 회전하는 호가 덮는 각도
    private const int SpinPeriodMs = 1100;        // 한 바퀴
    private const int SpinFrameMs = 16;
    private const float BadgeRadiusDp = 4.5f;
    private const float BadgeOutlineDp = 1.5f;
    /// <summary>링·음표를 살짝 죽여 화면을 덜 방해하는 정도(상태와 무관하게 일정).</summary>
    private const float RingAlpha = 0.7f;
    private const float GlyphAlpha = 0.85f;

    /// <summary>가사가 없을 때의 링·음표 색(회색).</summary>
    public static readonly Color MutedColor = Color.Argb(0xFF, 0x8A, 0x8A, 0x8A);
    /// <summary>배지를 링에서 떼어 보이게 하는 외곽색.</summary>
    private static readonly Color BadgeOutline = Color.Argb(0xFF, 0x14, 0x14, 0x14);

    private readonly float _density;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly RectF _arcRect = new();
    private readonly Handler _handler = new(Looper.MainLooper!);
    private Java.Lang.IRunnable? _tick;

    private Color _ringColor = MutedColor;
    private Color _fillColor = Color.Argb(0x73, 0x00, 0x00, 0x00);
    private Color _accentColor = Color.Argb(0xFF, 0xFF, 0xEB, 0x3B);
    private Color? _badgeColor;
    private bool _active;   // 오버레이(가사 밴드)가 지금 보이는가 — 링 굵기·이중선으로만 표기
    private bool _spinning;
    private long _spinStartMs;
    private float _angle = -90f;

    public BubbleStatusDrawable(float density) => _density = density;

    /// <summary>음표 글리프에 쓸 색 — 채워진 상태면 어두운색, 아니면 링 색과 같다.</summary>
    public Color GlyphColor { get; private set; } = MutedColor;

    /// <summary>
    /// 상태를 한 번에 반영한다.
    /// </summary>
    /// <param name="state">가사 상태(링·음표 색).</param>
    /// <param name="overlayVisible">가사 밴드가 지금 화면에 보이는가(링 굵기·이중선).</param>
    /// <param name="activeColor">가사를 찾았을 때 쓸 색(사용자 가사 색, 기본 흰색).</param>
    /// <param name="accentColor">검색 중 회전 호 색(사용자 카라오케 색).</param>
    /// <param name="fillColor">안쪽 채움 — 오버레이 배경 설정을 그대로 넘긴다(상태와 무관).</param>
    /// <param name="badgeColor">우상단 점 색. null이면 점을 그리지 않는다.</param>
    public void SetStatus(
        BubbleLyricsState state, bool overlayVisible,
        Color activeColor, Color accentColor, Color fillColor, Color? badgeColor)
    {
        _ringColor = state == BubbleLyricsState.Found ? activeColor : MutedColor;
        _accentColor = accentColor;
        _fillColor = fillColor;
        _active = overlayVisible;
        GlyphColor = Fade(_ringColor, GlyphAlpha);
        _badgeColor = badgeColor;
        SetSpinning(state == BubbleLyricsState.Searching);
        InvalidateSelf();
    }

    /// <summary>뷰를 떼기 전에 호출해 틱을 멈춘다(호출하지 않으면 프레임 예약이 남는다).</summary>
    public void Stop() => SetSpinning(false);

    public override void Draw(Canvas canvas)
    {
        var bounds = Bounds;
        float cx = bounds.CenterX(), cy = bounds.CenterY();
        var ringWidth = RingWidthDp * _density;
        var radius = Math.Min(bounds.Width(), bounds.Height()) / 2f - ringWidth / 2f - 0.5f;
        if (radius <= 0) return;

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = _fillColor;
        canvas.DrawCircle(cx, cy, radius, _paint);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = ringWidth;
        _paint.StrokeCap = Paint.Cap.Butt;
        // 검색 중에는 링을 트랙(흐리게)으로 깔고 그 위로 악센트 호가 돈다.
        _paint.Color = Fade(_ringColor, _spinning ? 0.30f : RingAlpha);
        canvas.DrawCircle(cx, cy, radius, _paint);

        if (_spinning)
        {
            // 회전 호는 움직임 자체가 신호라 흐리게 하지 않는다.
            _arcRect.Set(cx - radius, cy - radius, cx + radius, cy + radius);
            _paint.StrokeCap = Paint.Cap.Round;
            _paint.Color = _accentColor;
            canvas.DrawArc(_arcRect, _angle, SpinSweepDegrees, false, _paint);
        }
        else if (_active)
        {
            // 안쪽 보조 링 — 굵기·밝기는 바깥 링과 똑같이 두고 **줄 수만** 늘린다.
            // 채움을 건드리지 않으면서 "지금 표시 중"을 알리는 두 번째 신호.
            var innerRadius = radius - ringWidth - InnerRingGapDp * _density;
            if (innerRadius > 0) canvas.DrawCircle(cx, cy, innerRadius, _paint);
        }

        if (_badgeColor is { } badge)
        {
            // 링 위 45°(우상단)에 얹는다 — 음표와 겹치지 않는 자리다.
            var bx = cx + radius * 0.707f;
            var by = cy - radius * 0.707f;
            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = BadgeOutline;
            canvas.DrawCircle(bx, by, (BadgeRadiusDp + BadgeOutlineDp) * _density, _paint);
            _paint.Color = badge;
            canvas.DrawCircle(bx, by, BadgeRadiusDp * _density, _paint);
        }
    }

    public override void SetAlpha(int alpha) => _paint.Alpha = alpha;

    public override void SetColorFilter(ColorFilter? colorFilter) => _paint.SetColorFilter(colorFilter);

    public override int Opacity => (int)Format.Translucent;

    private static Color Fade(Color color, float factor) =>
        Color.Argb((int)(color.A * factor), color.R, color.G, color.B);

    private void SetSpinning(bool on)
    {
        if (_spinning == on) return;
        _spinning = on;
        if (on)
        {
            _spinStartMs = SystemClock.UptimeMillis();
            _tick = new Java.Lang.Runnable(Tick);
            _handler.Post(_tick);
        }
        else if (_tick is not null)
        {
            _handler.RemoveCallbacks(_tick);
            _tick = null;
            _angle = -90f;
        }
    }

    private void Tick()
    {
        if (!_spinning || _tick is null) return;
        var elapsed = SystemClock.UptimeMillis() - _spinStartMs;
        _angle = elapsed % SpinPeriodMs * 360f / SpinPeriodMs - 90f;
        InvalidateSelf();
        _handler.PostDelayed(_tick, SpinFrameMs);
    }
}
