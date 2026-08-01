using Musebase.Engine;

namespace Musebase.Android.Services;

// 광고 **판정 규칙**(AdSignals)은 Musebase.Engine으로 옮겼다 — Windows도 같은 규칙으로
// 광고 구간에는 가사를 찾지 않아야 해서다. 여기 남은 것은 안드로이드 뮤트용 상태 기계뿐이다.

/// <summary>한 번의 관측 결과. <c>Unknown</c>이 있는 이유는 <see cref="AdDecision"/> 주석 참고.</summary>
public enum AdSignal
{
    /// <summary>광고가 아니다(실제 곡이 재생 중이거나 대상 앱이 아님).</summary>
    NotAd,

    /// <summary>광고가 재생 중이다.</summary>
    Ad,

    /// <summary>재생이 멈춰 있어 알 수 없다 — 직전 판정을 유지해야 한다.</summary>
    Unknown,
}

/// <summary>
/// 노이즈 섞인 광고 신호를 안정된 상태로 바꾼다. 두 가지 보호 장치는 Windows판(Mutefy)에서
/// 실측으로 필요성이 확인된 것이다(코드는 새로 썼다).
///
/// <b>진입 디바운스</b> — 곡이 바뀌는 순간 메타데이터가 잠깐 비는데, 그게 광고와 구분되지 않는다.
/// 판정이 유지돼야 뮤트한다. 반대로 <b>이탈은 즉시</b> — 음악이 계속 뮤트돼 있는 쪽이 더 나쁜 실패다.
///
/// <b>안전 상한</b> — 광고 도중 감지가 죽으면 기기 미디어 볼륨이 0으로 남는다. 이 시간을 넘기면
/// 강제로 빠져나오고, 감지가 "광고 아님"을 한 번 보고할 때까지 재진입을 막는다.
/// 실측 광고 길이가 55·58초였으므로 90초는 너무 짧다.
///
/// <b>일시정지 보류(<see cref="AdSignal.Unknown"/>)</b> — Spotify 광고는 보통 2개 연속인데,
/// 그 사이에 재생이 잠깐 끊긴다. 이때 "재생 안 함 = 광고 아님"으로 보면 볼륨이 1.3초 돌아왔다가
/// 다시 내려가 광고 소리가 새어 나온다(실측). 그래서 재생이 멈춘 동안은 판정을 유지하되,
/// 사용자가 광고 중 일시정지하고 자리를 뜬 경우까지 붙잡고 있지 않도록
/// <see cref="PauseHold"/>로 제한한다.
///
/// 시계를 주입받으므로 합성 시간으로 검증할 수 있다.
/// </summary>
public sealed class AdDecision
{
    /// <summary>
    /// 재생이 멈춘 동안 직전 판정을 붙잡고 있는 최대 시간. 광고 사이 공백(실측 1.3초)은 충분히
    /// 덮으면서, 사용자가 광고 중 일시정지하고 떠나면 이 시간 뒤엔 볼륨을 되돌린다.
    /// </summary>
    public static readonly TimeSpan PauseHold = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _enterDebounce;
    private readonly TimeSpan _maxAdDuration;

    private DateTime? _candidateSince;
    private DateTime _adSince;
    private DateTime? _unknownSince;
    private bool _suppressed;

    public AdDecision(TimeSpan enterDebounce, TimeSpan maxAdDuration)
    {
        _enterDebounce = enterDebounce;
        _maxAdDuration = maxAdDuration;
    }

    /// <summary>현재 확정 상태(디바운스·안전 상한 반영 후).</summary>
    public bool IsAd { get; private set; }

    /// <summary>안전 상한이 발동한 횟수. 0이 아니면 감지가 실패했다는 뜻이라 기록할 가치가 있다.</summary>
    public int SafetyTrips { get; private set; }

    /// <summary>원시 신호 1개를 넣는다. <see cref="IsAd"/>가 바뀌면 true.</summary>
    public bool Advance(AdSignal signal, DateTime now)
    {
        // 상한을 먼저 본다 — 신호가 계속 광고라고 우겨도, 재생이 멈춰 보류 중이어도 여기서 빠져나온다.
        if (IsAd && now - _adSince >= _maxAdDuration)
        {
            _suppressed = true;
            _candidateSince = null;
            _unknownSince = null;
            SafetyTrips++;
            IsAd = false;
            return true;
        }

        // 재생이 멈춘 동안은 판정을 유지한다(광고 사이 공백). 다만 무한정은 아니다.
        if (signal == AdSignal.Unknown)
        {
            _unknownSince ??= now;
            if (now - _unknownSince < PauseHold) return false;

            // 너무 오래 멈춰 있다 — 광고가 아니라고 보고 볼륨을 되돌린다.
            signal = AdSignal.NotAd;
        }
        else
        {
            _unknownSince = null;
        }

        var rawIsAd = signal == AdSignal.Ad;

        // 직전 안전 상한 발동은 감지가 "광고 아님"을 한 번 말할 때까지 유효하다.
        if (_suppressed)
        {
            if (rawIsAd) rawIsAd = false;
            else _suppressed = false;
        }

        if (rawIsAd)
        {
            _candidateSince ??= now;
            if (now - _candidateSince < _enterDebounce) return false;
        }
        else
        {
            _candidateSince = null;
        }

        if (IsAd == rawIsAd) return false;

        IsAd = rawIsAd;
        if (rawIsAd) _adSince = now;
        return true;
    }

    /// <summary>기능을 끌 때 등, 상태를 초기화한다.</summary>
    public void Reset()
    {
        IsAd = false;
        _candidateSince = null;
        _unknownSince = null;
        _suppressed = false;
    }
}
