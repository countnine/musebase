using Microsoft.AspNetCore.Http;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 의미 엔진 카드의 폼 값. 번역 카드(<see cref="TranslationForm"/>)와 같은 규칙이다 —
/// ① 빈 키 칸은 "그대로 두기" ② 체크박스는 숨은 마커(<c>fallbackSubmitted</c>)가 있을 때만 해석한다.
/// </summary>
public sealed record MeaningForm(
    string? Engine,
    string? GeminiKey, string? GeminiModel, string? OpenRouterKey, string? OpenRouterModel,
    string? Fallback, bool? Auto, long? GeminiCap, int? AutoCap)
{
    public static MeaningForm Read(IFormCollection form)
    {
        var submitted = form["fallbackSubmitted"].ToString() == "1";
        return new MeaningForm(
            Engine: form["engine"].ToString(),
            GeminiKey: form["geminiKey"].ToString(),
            GeminiModel: form["geminiModel"].ToString(),
            OpenRouterKey: form["openRouterKey"].ToString(),
            OpenRouterModel: form["openRouterModel"].ToString(),
            Fallback: submitted ? string.Join(",", form["fallback"].ToArray()) : null,
            Auto: submitted ? form["auto"].ToString() == "1" : null,
            GeminiCap: long.TryParse(form["geminiCap"].ToString(), out var gc) ? gc : null,
            AutoCap: int.TryParse(form["autoCap"].ToString(), out var ac) ? ac : null);
    }

    public IReadOnlyList<(string Engine, string Reason)> MalformedKeys() =>
        new (string Engine, string? Key)[] { ("gemini", GeminiKey), ("openrouter", OpenRouterKey) }
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .Select(k => (k.Engine, Reason: MeaningCheckBook.KeyFormatProblem(k.Engine, k.Key!)))
            .Where(k => k.Reason is not null)
            .Select(k => (k.Engine, k.Reason!))
            .ToList();

    /// <summary>형식이 틀린 키 칸을 비운 폼(= 저장하면 그 키는 그대로)과 비운 이유.</summary>
    public (MeaningForm Clean, IReadOnlyList<(string Engine, string Reason)> Rejected) WithoutMalformedKeys()
    {
        var bad = MalformedKeys();
        bool Bad(string engine) => bad.Any(b => b.Engine == engine);
        return (this with
        {
            GeminiKey = Bad("gemini") ? "" : GeminiKey,
            OpenRouterKey = Bad("openrouter") ? "" : OpenRouterKey,
        }, bad);
    }

    /// <summary>지금 설정 위에 폼 값을 얹은 구성(저장하지 않는다). 빈 칸은 지금 값을 쓴다.</summary>
    public MeaningOptions Overlay(MeaningOptions current)
    {
        static string? Pick(string? typed, string? now) => string.IsNullOrWhiteSpace(typed) ? now : typed.Trim();
        return current with
        {
            Engine = Pick(Engine, current.Engine)!,
            GeminiApiKey = Pick(GeminiKey, current.GeminiApiKey),
            GeminiModel = Pick(GeminiModel, current.GeminiModel),
            OpenRouterApiKey = Pick(OpenRouterKey, current.OpenRouterApiKey),
            OpenRouterModel = Pick(OpenRouterModel, current.OpenRouterModel),
            Fallback = Fallback is null ? current.Fallback : Fallback.Length == 0 ? MeaningWriterRegistry.None : Fallback,
            AutoGenerate = Auto ?? current.AutoGenerate,
            GeminiMonthlyCap = GeminiCap ?? current.GeminiMonthlyCap,
            AutoMonthlyCap = AutoCap ?? current.AutoMonthlyCap,
        };
    }

    /// <summary>확인할 엔진 — 체인 전부 + 이번에 키를 넣은 엔진.</summary>
    public IReadOnlyList<string> EnginesToCheck(MeaningOptions options)
    {
        var engines = options.ChainIds.ToList();
        if (!string.IsNullOrWhiteSpace(GeminiKey)) engines.Add("gemini");
        if (!string.IsNullOrWhiteSpace(OpenRouterKey)) engines.Add("openrouter");
        return engines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
