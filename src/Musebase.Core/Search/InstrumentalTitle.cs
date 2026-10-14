using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// 제목만 보고 <b>연주곡일 가능성이 높은가</b>를 판단한다("Song (Instrumental)", "Song - Inst.", "伴奏").
///
/// 확정이 아니라 힌트다 — 쓰는 곳은 셋이다.
/// ① 제공자 검색: 연주곡 제목이면 후보 제목도 연주곡 표기일 때만 채택한다. 정제 검색(괄호 떼기)이
///    보컬 원곡 가사를 찾아와 연주곡 위에 가사가 붙던 문제를 막는다.
/// ② 가사 서버 조회·저장: 연주곡 제목은 느슨한 키로 원곡 행에 맞추지 않는다(반대 방향도).
/// ③ 아무 후보도 못 고르면 "가사 없음" 대신 "연주곡"으로 표시한다.
///
/// 노래방 반주(karaoke)는 넣지 않는다 — 가사를 보며 따라 부르려고 트는 경우가 많다.
/// </summary>
public static class InstrumentalTitle
{
    // "inst"는 단독 단어일 때만(괄호·대괄호·대시 뒤) — "Instinct"·"Install" 같은 제목을 잡지 않는다.
    private static readonly Regex Pattern = new(
        @"\binstrumentals?\b|[\(\[\-–—/]\s*inst\.?\s*(?:ver(?:sion|\.)?\s*)?(?:[\)\]]|$)|\boff[\s\-]?vocal\b|伴奏|纯音乐|純音樂|연주곡",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>제목에 연주곡 표기가 있는가.</summary>
    public static bool Looks(string? title) => !string.IsNullOrWhiteSpace(title) && Pattern.IsMatch(title);
}
