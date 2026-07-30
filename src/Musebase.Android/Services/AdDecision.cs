namespace Musebase.Android.Services;

/// <summary>
/// 광고 판정 규칙. Android 타입에 의존하지 않는다 — 나중에 코어 변경 요청으로 Engine에 옮겨
/// 유닛 테스트를 붙일 수 있게 하기 위함(현재 Musebase.Android는 sln 밖이라 CI 테스트가 없다).
///
/// 신호를 신뢰도 순으로 겹친다. 1·2번은 Spotify가 시스템 계약에 맞춰 내보내는 값이라
/// 지역·언어와 무관하고, 3번만 추측이다.
/// </summary>
public static class AdSignals
{
    /// <summary>
    /// 표준 광고 메타데이터 키. 플랫폼 상수(<c>MediaMetadata.METADATA_KEY_ADVERTISEMENT</c>)를
    /// 그대로 쓰지 못하고 문자열을 박은 이유: <b>.NET for Android 바인딩에 이 상수가 없다</b>
    /// (Microsoft.Android.Ref.34의 <c>Android.Media.MediaMetadata</c>에는 <c>MetadataKeyMediaId</c>
    /// 등은 있지만 <c>MetadataKeyAdvertisement</c>는 없다 — 직접 확인함).
    /// 값 자체는 안드로이드 플랫폼 계약이라 바뀌지 않는다.
    /// </summary>
    public const string AdvertisementMetadataKey = "android.media.metadata.ADVERTISEMENT";

    /// <summary>위 키가 광고를 뜻하는 값.</summary>
    private const long AdvertisementFlagSet = 1;

    /// <summary>Spotify가 광고 구간에 쓰는 미디어 ID 접두사. macOS 원본이 쓰던 것과 같은 신호.</summary>
    private const string SpotifyAdMediaIdPrefix = "spotify:ad";

    /// <summary>
    /// Windows(SMTC)에서 실측 검증된 폴백. 앨범 조건이 안전장치다 — Spotify의 실제 곡은 앨범이
    /// 항상 채워져 있으므로, 이 이름으로 발매된 진짜 곡이 있어도 뮤트되지 않는다.
    ///
    /// <b>안드로이드에서는 이 표가 맞지 않는다</b> — 실측 결과 광고의 아티스트는
    /// <c>'광고 • 1/2'</c>(현지화 + 순번)였다. 순번이 붙어 고정 문자열로 잡을 수 없고 언어마다
    /// 다르다. 그래도 지우지 않는 이유는 이게 신호 ③이기 때문이다: ①(플래그)과 ②(mediaId)가
    /// 둘 다 오는 것을 확인했으므로 실무상 여기까지 내려올 일이 없고, 만약 Spotify가 ①②를
    /// 빼면 최소한 다른 지역/버전에서 걸릴 여지를 남겨 둔다.
    /// </summary>
    private static readonly string[] AdArtists = { "Spotify", "Sponsored Message" };

    /// <param name="advertisementFlag">`METADATA_KEY_ADVERTISEMENT` 값(없으면 0).</param>
    /// <param name="mediaId">`METADATA_KEY_MEDIA_ID` 값.</param>
    public static bool LooksLikeAd(long advertisementFlag, string? mediaId, string? artist, string? album)
    {
        // 1) 시스템 표준 광고 플래그 — 있으면 이게 결론이다.
        if (advertisementFlag == AdvertisementFlagSet) return true;

        // 2) Spotify 고유 광고 URI.
        if (!string.IsNullOrWhiteSpace(mediaId) &&
            mediaId!.TrimStart().StartsWith(SpotifyAdMediaIdPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        // 3) 문자열 폴백. 앨범이 비어 있을 때만 본다.
        if (!string.IsNullOrWhiteSpace(album)) return false;
        if (string.IsNullOrWhiteSpace(artist)) return false;

        var trimmed = artist!.Trim();
        foreach (var candidate in AdArtists)
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}

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
