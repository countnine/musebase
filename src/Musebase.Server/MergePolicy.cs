using Musebase.Core;

namespace Musebase.Server;

/// <summary>
/// LRC에서 뽑아낸, 병합 판정과 응답 필드에 쓰는 사실들.
/// 서버는 LRC 문자열 자체는 변형하지 않고 읽기 전용으로만 파싱한다.
/// </summary>
public sealed record LyricsFacts(int LineCount, bool HasInlineTimeTags, string[] Langs)
{
    public static readonly LyricsFacts Empty = new(0, false, Array.Empty<string>());

    /// <summary>
    /// 확장 LRC를 파싱해 줄 수·글자단위 타임태그 유무·번역 언어를 뽑는다.
    /// 파싱에 실패하면(유효 라인 0) <see cref="Empty"/>.
    /// 번역 태그는 <c>tr</c>(제공자 번역) 또는 <c>tr:{lang}</c> 형태다
    /// (<see cref="LineAttachments.TranslationTag"/>).
    /// </summary>
    public static LyricsFacts From(string lrc)
    {
        var parsed = Lyrics.Parse(lrc);
        if (parsed is null) return Empty;

        var langs = new List<string>();
        var hasInline = false;
        foreach (var tag in parsed.Metadata.AttachmentTags)
        {
            if (tag == LineAttachments.TagTimeTag) { hasInline = true; continue; }
            if (!tag.StartsWith(LineAttachments.TagTranslationPrefix, StringComparison.Ordinal)) continue;

            // "tr" = 제공자 기본 번역(언어 미상), "tr:ko" = 대상 언어 번역
            var colon = tag.IndexOf(':');
            var lang = colon >= 0 ? tag[(colon + 1)..] : "";
            if (lang.Length > 0 && !langs.Contains(lang, StringComparer.OrdinalIgnoreCase)) langs.Add(lang);
            else if (lang.Length == 0 && !langs.Contains("*")) langs.Add("*"); // 언어 미상 제공자 번역
        }

        langs.Sort(StringComparer.Ordinal);
        return new LyricsFacts(parsed.Lines.Count, hasInline, langs.ToArray());
    }
}

/// <summary>
/// PUT 병합 판정(순수 함수 — 유닛 테스트 대상). 계약은 `contracts/lyrics-api.md`의 "병합 정책".
/// </summary>
public static class MergePolicy
{
    /// <summary>글자 단위 카라오케는 줄 몇 개보다 훨씬 가치가 크다 — 퇴화를 막기 위한 가중치.</summary>
    private const int InlineTimeTagWeight = 50;
    private const int LangWeight = 20;
    /// <summary>측정 오차·표기 차이로 인한 사소한 감소는 거부하지 않는다.</summary>
    private const int PoorerThreshold = 5;

    public enum Decision { Accept, RejectUserEditProtected, RejectPoorerContent }

    /// <summary>
    /// 기존 행(<paramref name="existing"/>, 없으면 null)과 들어온 값을 비교해 저장 여부를 정한다.
    /// </summary>
    public static Decision Evaluate(
        (string Origin, LyricsFacts Facts)? existing,
        string incomingOrigin,
        LyricsFacts incomingFacts)
    {
        if (existing is not { } current) return Decision.Accept; // 새 곡

        // 사용자가 손으로 고친 가사는 다른 기기의 자동 검색이 덮어쓰지 못한다.
        var currentIsUser = string.Equals(current.Origin, LyricsEntry.OriginUser, StringComparison.OrdinalIgnoreCase);
        var incomingIsUser = string.Equals(incomingOrigin, LyricsEntry.OriginUser, StringComparison.OrdinalIgnoreCase);
        if (currentIsUser && !incomingIsUser) return Decision.RejectUserEditProtected;
        if (incomingIsUser) return Decision.Accept; // 사용자 편집본은 항상 채택

        // 둘 다 provider — 명백히 빈약해지는 갱신은 막는다(카라오케 태그 소실·줄 수 급감 등).
        return Score(incomingFacts) + PoorerThreshold < Score(current.Facts)
            ? Decision.RejectPoorerContent
            : Decision.Accept;
    }

    /// <summary>풍부함 점수 — 줄 수 + 글자단위 타임태그 + 번역 언어 수.</summary>
    public static int Score(LyricsFacts facts) =>
        facts.LineCount
        + (facts.HasInlineTimeTags ? InlineTimeTagWeight : 0)
        + facts.Langs.Length * LangWeight;
}
