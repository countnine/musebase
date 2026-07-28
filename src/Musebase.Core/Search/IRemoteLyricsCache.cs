namespace Musebase.Core.Search;

/// <summary>
/// 원격 가사 캐시(개인 서버)의 계약. 로컬 캐시와 제공자 검색 사이에 끼어드는 **가산 계층**이다 —
/// 서버가 없거나 못 붙으면 조용히 기존 동작(제공자 검색)으로 강등되어야 하므로,
/// 구현은 **예외를 밖으로 던지지 않는다**(실패·타임아웃·미접속 = 조회 null / 저장 무시).
///
/// HTTP 계약은 `contracts/lyrics-api.md`(v1), 참조 구현은 <see cref="HttpRemoteLyricsCache"/>.
/// </summary>
public interface IRemoteLyricsCache
{
    /// <summary>서버에서 가사를 가져온다. 미스·실패·타임아웃은 모두 null.</summary>
    Task<Lyrics?> GetAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>서버에 가사를 올린다(업서트). 실패는 조용히 무시하므로 호출자가 await하지 않아도 된다.</summary>
    Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default);
}
