using Microsoft.AspNetCore.Http;
using Musebase.Core.Translation;

namespace Musebase.Server;

/// <summary>
/// 번역 엔진 카드의 폼 값. 저장과 "저장하지 않고 테스트"가 같은 해석을 쓴다.
///
/// 규칙 두 가지를 여기 모아 둔다.
/// ① <b>빈 키 칸은 "그대로 두기"</b>다 — 키는 끝 네 글자만 보여 주므로, 모델만 바꾸려고 저장할
///    때마다 긴 키를 다시 치게 하면 안 된다.
/// ② <b>체크박스는 꺼져 있으면 전송되지 않는다</b> — 숨은 마커(<c>fallbackSubmitted</c>)가 있는데
///    값이 없으면 "폴백 없음"·"자동 번역 끔"이다.
/// </summary>
public sealed record TranslationForm(
    string? Engine, string? Lang,
    string? DeeplKey, string? GoogleKey, string? OpenRouterKey, string? OpenRouterModel,
    string? LibreEndpoint, string? LibreKey, string? MyMemoryEmail,
    string? Fallback, bool? Auto)
{
    public static TranslationForm Read(IFormCollection form)
    {
        var submitted = form["fallbackSubmitted"].ToString() == "1";
        return new TranslationForm(
            Engine: form["engine"].ToString(),
            Lang: form["lang"].ToString(),
            DeeplKey: form["deeplKey"].ToString(),
            GoogleKey: form["googleKey"].ToString(),
            OpenRouterKey: form["openRouterKey"].ToString(),
            OpenRouterModel: form["openRouterModel"].ToString(),
            LibreEndpoint: form["libreEndpoint"].ToString(),
            LibreKey: form["libreKey"].ToString(),
            MyMemoryEmail: form["myMemoryEmail"].ToString(),
            Fallback: submitted ? string.Join(",", form["fallback"].ToArray()) : null,
            Auto: submitted ? form["auto"].ToString() == "1" : null);
    }

    /// <summary>새로 넣은 키 중 형식이 틀린 것 — (엔진, 이유).</summary>
    public IReadOnlyList<(string Engine, string Reason)> MalformedKeys() =>
        new (string Engine, string? Key)[]
            {
                ("deepl", DeeplKey), ("google", GoogleKey), ("openrouter", OpenRouterKey),
            }
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .Select(k => (k.Engine, Reason: TranslationOptions.KeyFormatProblem(k.Engine, k.Key!)))
            .Where(k => k.Reason is not null)
            .Select(k => (k.Engine, k.Reason!))
            .ToList();

    /// <summary>
    /// 형식이 틀린 키 칸을 비운 폼(= 저장하면 그 키는 "그대로 두기")과 비운 이유.
    /// 틀린 키를 저장해 두면 다음 작업이 조용히 실패한다 — 저장 전에 거른다.
    /// </summary>
    public (TranslationForm Clean, IReadOnlyList<(string Engine, string Reason)> Rejected) WithoutMalformedKeys()
    {
        var bad = MalformedKeys();
        bool Bad(string engine) => bad.Any(b => b.Engine == engine);
        return (this with
        {
            DeeplKey = Bad("deepl") ? "" : DeeplKey,
            GoogleKey = Bad("google") ? "" : GoogleKey,
            OpenRouterKey = Bad("openrouter") ? "" : OpenRouterKey,
        }, bad);
    }

    /// <summary>
    /// 지금 설정 위에 폼 값을 얹은 구성(저장하지 않는다). 빈 칸은 지금 값을 그대로 쓴다.
    /// </summary>
    public TranslationOptions Overlay(TranslationOptions current)
    {
        static string? Pick(string? typed, string? now) => string.IsNullOrWhiteSpace(typed) ? now : typed.Trim();

        return current with
        {
            Engine = Pick(Engine, current.Engine)!,
            Lang = TranslationOptions.NormalizeLang(Pick(Lang, current.Lang)),
            DeeplApiKey = Pick(DeeplKey, current.DeeplApiKey),
            GoogleApiKey = Pick(GoogleKey, current.GoogleApiKey),
            OpenRouterApiKey = Pick(OpenRouterKey, current.OpenRouterApiKey),
            OpenRouterModel = Pick(OpenRouterModel, current.OpenRouterModel),
            LibreEndpoint = Pick(LibreEndpoint, current.LibreEndpoint),
            LibreApiKey = Pick(LibreKey, current.LibreApiKey),
            MyMemoryEmail = Pick(MyMemoryEmail, current.MyMemoryEmail),
            Fallback = Fallback ?? current.Fallback,
            AutoTranslate = Auto ?? current.AutoTranslate,
        };
    }

    /// <summary>
    /// 확인할 엔진 — 체인 구성원 전부 + 이번에 키를 넣은 엔진. 체인에 없더라도 키를 새로
    /// 넣었으면 그 키가 맞는지 알고 싶은 것이 사람의 뜻이다. 공개 무키 엔진은 확인하지 않는다
    /// (확인할 키가 없고, 한 번 부를 때마다 공개 서버의 무료 한도를 쓴다).
    /// </summary>
    public IReadOnlyList<string> EnginesToCheck(TranslationOptions options)
    {
        var engines = options.ChainIds.ToList();
        if (!string.IsNullOrWhiteSpace(DeeplKey)) engines.Add("deepl");
        if (!string.IsNullOrWhiteSpace(GoogleKey)) engines.Add("google");
        if (!string.IsNullOrWhiteSpace(OpenRouterKey)) engines.Add("openrouter");
        return engines
            .Where(e => TranslatorRegistry.Find(e) is { RequiresApiKey: true } || options.ChainIds.Contains(e))
            .Where(e => !string.Equals(e, "mymemory", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>알림 한 줄 — 저장/확인 결과를 엔진별 ✓·✗로.</summary>
    public static string Notice(
        string head, IReadOnlyList<(string Engine, string Reason)> rejected, IReadOnlyList<EngineCheck> checks)
    {
        var parts = new List<string> { head };
        foreach (var (engine, reason) in rejected)
            parts.Add($"✗ {engine} 키를 저장하지 않았습니다 — {reason}");
        foreach (var c in checks)
            parts.Add(c.Ok ? $"✓ {c.Engine} 정상({c.Millis}ms)" : $"✗ {c.Engine}: {c.Detail}");
        return string.Join(" · ", parts);
    }
}
