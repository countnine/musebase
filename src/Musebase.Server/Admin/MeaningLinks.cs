namespace Musebase.Server;

/// <summary>
/// 곡의 배경·의미를 사람이 직접 읽으러 갈 외부 사이트 링크.
///
/// Musixmatch의 "Meaning" 섹션은 **API로 가져올 수 없다** — 공개 API에는 meaning 엔드포인트가
/// 없고(그 섹션은 사용자 기여 웹 콘텐츠다), 크롤링은 약관 위반이라 링크만 건다.
/// Genius는 공식 API로 곡 설명을 받아올 수 있으므로, 수집이 끝난 곡은 검색 링크 대신
/// 정확한 곡 페이지(<c>song.url</c>)로 승격한다.
///
/// 순수 함수라 유닛 테스트 대상이다. 관리자 페이지의 CSP(<c>default-src 'none'</c>)는
/// 링크 이동에 관여하지 않으므로 <c>&lt;a href&gt;</c>는 그대로 동작한다.
/// </summary>
public static class MeaningLinks
{
    /// <summary>검색어 — "아티스트 제목". 아티스트가 없으면 제목만.</summary>
    public static string Query(string title, string artist)
    {
        var t = (title ?? "").Trim();
        var a = (artist ?? "").Trim();
        return a.Length == 0 ? t : $"{a} {t}";
    }

    public static string MusixmatchSearch(string title, string artist) =>
        "https://www.musixmatch.com/search/" + Uri.EscapeDataString(Query(title, artist));

    public static string GeniusSearch(string title, string artist) =>
        "https://genius.com/search?q=" + Uri.EscapeDataString(Query(title, artist));

    /// <summary>수집으로 알아낸 정확한 Genius 곡 페이지가 있으면 그것을, 없으면 검색 링크를 준다.</summary>
    public static string Genius(string title, string artist, string? knownUrl) =>
        string.IsNullOrWhiteSpace(knownUrl) ? GeniusSearch(title, artist) : knownUrl!;
}
