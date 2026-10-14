namespace Musebase.Core.Translation;

/// <summary>
/// 가사가 대상 언어로 "번역돼 있다"고 볼 수 있는가.
///
/// 예전에는 대상 언어 태그(<c>tr:ko</c>)가 <b>한 줄이라도</b> 있으면 번역된 곡으로 쳤다. 그런데 기기는
/// 엔진이 꺼져 있거나 API 번역을 막아 두어도 <b>로컬 캐시에 있던 줄만</b> 채워 올린다 — 중국 제공자
/// 가사의 "Produced by：…" 같은 크레딧 줄이 흔하다. 그러면 61줄 중 1줄만 번역된 곡이 서버에서는
/// 자동 번역 대상에서 빠지고, 앱에서는 서버 번역을 기다리지 않는다(실제로 일어났다). 그래서
/// <b>번역이 필요한 줄 중 얼마가 채워졌는가</b>로 판단한다.
///
/// 번역이 필요 없는 줄은 세지 않는다: 글자가 없는 줄(♪, 숫자만), 그리고 <b>이미 대상 언어 문자로
/// 쓰인 줄</b>(대상이 한국어인데 한글이 든 줄 — 한국 노래의 본문). 문자로 언어를 가를 수 있는
/// 한국어·일본어만 이렇게 하고, 라틴 문자 언어끼리는 구별하지 않는다.
/// </summary>
public static class TranslationCoverage
{
    /// <summary>번역이 필요한 줄 중 이만큼 채워져 있으면 번역된 곡으로 본다.</summary>
    public const double Enough = 0.8;

    /// <summary>번역이 필요한 줄 수와 그중 대상 언어 번역이 붙은 줄 수.</summary>
    public static (int Needed, int Translated) Count(Lyrics lyrics, string targetLang)
    {
        var lang = targetLang.ToLowerInvariant();
        var tag = LineAttachments.TranslationTag(lang);
        int needed = 0, translated = 0;
        foreach (var line in lyrics.Lines)
        {
            if (!NeedsTranslation(line.Content, lang)) continue;
            needed++;
            if (line.Attachments[tag] is not null) translated++;
        }
        return (needed, translated);
    }

    /// <summary>번역이 필요한 줄의 <see cref="Enough"/> 이상이 채워졌는가(필요한 줄이 없으면 true).</summary>
    public static bool IsTranslated(Lyrics lyrics, string targetLang)
    {
        var (needed, translated) = Count(lyrics, targetLang);
        return needed == 0 || translated >= needed * Enough;
    }

    private static bool NeedsTranslation(string? content, string lang)
    {
        if (string.IsNullOrWhiteSpace(content) || !content.Any(char.IsLetter)) return false;
        return lang switch
        {
            "ko" => !content.Any(IsHangul),
            "ja" => !content.Any(IsKana),
            _ => true,
        };
    }

    private static bool IsHangul(char c) =>
        c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ';
}
