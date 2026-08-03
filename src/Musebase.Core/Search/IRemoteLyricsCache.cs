namespace Musebase.Core.Search;

/// <summary>
/// 원격 조회 1회의 결과. 미스여도 "다른 기기가 지금 같은 곡을 찾고 있다"는 힌트가 붙을 수 있어
/// 단순 <c>Lyrics?</c>로는 부족하다(`contracts/lyrics-api.md`의 "번역 양보").
/// </summary>
/// <param name="Lyrics">받은 가사. 미스·실패면 null.</param>
/// <param name="Pending">최근에 다른 기기도 이 곡을 물었다 — 번역을 잠시 양보할 만하다.</param>
/// <param name="RetryAfterMs">서버가 제안하는 재조회 간격(0이면 제안 없음). 호출자가 clamp한다.</param>
/// <param name="Langs">받은 가사에 들어 있는 번역 언어들(소문자). 히트가 아니면 빈 배열.</param>
public readonly record struct RemoteLyricsResult(
    Lyrics? Lyrics, bool Pending, int RetryAfterMs, IReadOnlyList<string> Langs)
{
    /// <summary>미스·실패·미접속 — 아무 힌트도 없다.</summary>
    public static readonly RemoteLyricsResult Miss = new(null, false, 0, []);

    /// <summary>대상 언어 번역이 들어 있는가(대소문자 무시).</summary>
    public bool HasLanguage(string lang) =>
        Langs.Contains(lang, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 원격 가사 캐시(개인 서버)의 계약. 로컬 캐시와 제공자 검색 사이에 끼어드는 **가산 계층**이다 —
/// 서버가 없거나 못 붙으면 조용히 기존 동작(제공자 검색)으로 강등되어야 하므로,
/// 구현은 **예외를 밖으로 던지지 않는다**(실패·타임아웃·미접속 = <see cref="RemoteLyricsResult.Miss"/> / 저장 무시).
///
/// HTTP 계약은 `contracts/lyrics-api.md`(v1), 참조 구현은 <see cref="HttpRemoteLyricsCache"/>.
/// </summary>
public interface IRemoteLyricsCache
{
    /// <summary>서버에서 가사를 가져온다. 미스·실패·타임아웃은 모두 <see cref="RemoteLyricsResult.Miss"/>.</summary>
    Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>서버에 가사를 올린다(업서트). 실패는 조용히 무시하므로 호출자가 await하지 않아도 된다.</summary>
    Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default);

    /// <summary>
    /// 곡의 의미를 가져온다. 없으면 null — 만들지는 않는다(생성은 서버 관리자만 한다).
    /// 가사와 마찬가지로 실패·미접속도 조용히 null이다.
    /// </summary>
    Task<SongMeaningView?> GetMeaningAsync(string title, string artist, CancellationToken ct = default);
}

/// <summary>
/// 앱이 보여 줄 "이 곡의 의미" 한 건.
///
/// <see cref="Attribution"/>은 <b>표시 의무</b>가 있다 — Wikipedia 본문은 CC BY-SA이고
/// Genius·Last.fm도 링크 표기를 요구한다(`contracts/lyrics-api.md`). 본문만 떼어 보여 주면 안 된다.
/// </summary>
/// <param name="Summary">대상 언어 한 문단.</param>
/// <param name="Attribution">출처 이름·링크 쌍.</param>
/// <param name="Lang">요약 언어(`ko` 등).</param>
public sealed record SongMeaningView(
    string Summary,
    IReadOnlyList<MeaningCredit> Attribution,
    string Lang)
{
    /// <summary>화면 하단에 그대로 붙일 한 줄(링크가 없는 UI용).</summary>
    public string CreditLine =>
        Attribution.Count == 0
            ? ""
            : "출처: " + string.Join(" · ", Attribution.Select(a => a.Name))
              + (Attribution.Any(a => a.Name == "Wikipedia") ? " (CC BY-SA)" : "");
}

/// <summary>출처 한 건.</summary>
public sealed record MeaningCredit(string Name, string? Url);
