using System.Text;

namespace Musebase.Core.Meaning;

/// <summary>
/// 수집한 영어 원문들을 읽고 "이 곡이 무엇에 대한 노래인지"를 대상 언어로 써 준다.
///
/// 번역이 아니라 **요약**이라 <see cref="Translation.ITranslator"/>로는 안 된다 — DeepL에 긴 영어
/// bio를 넣으면 긴 한국어 문서가 나올 뿐 "의미"가 되지 않는다. 구현은 순수 HTTP + JSON이며
/// 엔진은 <see cref="MeaningWriterRegistry"/>로 갈아끼운다(번역 엔진과 같은 구조).
///
/// 실패는 예외가 아니라 null이다 — 의미는 부가 기능이고, 없다고 가사가 안 뜨면 안 된다.
/// </summary>
public interface IMeaningWriter
{
    /// <summary>레지스트리 id(예: "gemini"). 어떤 엔진으로 만들었는지 기록해 둔다.</summary>
    string EngineId { get; }

    /// <summary>실제로 호출한 모델 이름(재생성 판단·기록용).</summary>
    string Model { get; }

    Task<string?> WriteAsync(
        string title, string artist, IReadOnlyList<MeaningSource> sources,
        string targetLang, CancellationToken ct = default);
}

/// <summary>
/// 엔진에 상관없이 같은 프롬프트를 쓴다 — 모델을 바꿔도 결과의 성격이 흔들리지 않게 하고,
/// 두 엔진의 출력을 나란히 비교할 수 있게 하기 위해서다.
/// </summary>
public static class MeaningPrompt
{
    /// <summary>원문이 아무리 길어도 이 길이까지만 넣는다(토큰 폭주 방지).</summary>
    public const int MaxSourceChars = 6000;

    /// <summary>
    /// 마지막 문장이 이 프롬프트의 핵심이다 — 자료가 부족할 때 모델이 지어내지 않고
    /// "부족하다"고 쓰게 만든다. 곡 해설은 그럴듯한 창작이 특히 쉬운 영역이다.
    /// </summary>
    public static string Build(
        string title, string artist, IReadOnlyList<MeaningSource> sources, string targetLang)
    {
        var language = LanguageName(targetLang);
        var sb = new StringBuilder();
        sb.Append("다음은 한 곡에 대해 여러 웹 자료에서 모은 설명이다.\n\n");
        sb.Append($"곡: {title}\n아티스트: {artist}\n\n");

        var budget = MaxSourceChars;
        foreach (var source in sources)
        {
            if (budget <= 0) break;
            var text = source.Text.Length > budget ? source.Text[..budget] : source.Text;
            budget -= text.Length;
            sb.Append($"[{source.Name}]\n{text}\n\n");
        }

        sb.Append($"""
            위 자료만 근거로, 이 곡이 무엇에 대한 노래인지 {language}로 3~5문장으로 써라.

            - 작곡 배경, 가사가 다루는 주제, 알려진 해석을 중심으로 쓴다.
            - 자료에 없는 내용은 절대 지어내지 않는다. 추측하지 않는다.
            - 자료가 부족해 의미를 말하기 어려우면, 그렇게만 한 문장으로 쓴다.
            - 차트 성적·수상 이력 같은 곡의 의미와 무관한 사실은 넣지 않는다.
            - 머리말 없이 본문만 쓴다.
            """);
        return sb.ToString();
    }

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "ko" => "한국어",
        "ja" => "일본어",
        "en" => "영어",
        "zh" or "zh-hans" or "zh-hant" => "중국어",
        _ => code,
    };
}
