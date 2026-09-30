using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 의미 엔진 키가 실제로 맞는지 화면에서 확인한다 — 번역(<see cref="EngineCheckBook"/>)과 같은 순서로
/// <b>형식 → 실제 호출</b>이다. 호출은 짧은 자료 한 줄로 한 문단을 쓰게 해 본다(곡당 생성보다 훨씬 싸다).
/// 결과는 메모리에만 둔다(재시작하면 "확인 안 함").
/// </summary>
public sealed class MeaningCheckBook
{
    private static readonly IReadOnlyList<MeaningSource> Probe =
    [
        new("check", null, "\"Yesterday\" is a 1965 song by the Beatles. Paul McCartney wrote it; "
                           + "the lyrics look back with regret on a love that has suddenly ended."),
    ];

    private readonly ConcurrentDictionary<string, EngineCheck> _last = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, EngineCheck> Last => _last;

    /// <summary>
    /// 키 형식 검사. Gemini는 AI Studio 키(<c>AIza…</c> 39자)와 새 형식(<c>AQ.…</c>)을 둘 다 받는다 —
    /// 둘 다 Gemini API에서 인증이 통과한다(2026-09 실측). OpenRouter는 번역과 같은 규칙.
    /// 비밀 본문은 말하지 않는다(앞 네 글자·길이만).
    /// </summary>
    public static string? KeyFormatProblem(string engine, string key)
    {
        var k = key.Trim();
        return engine.ToLowerInvariant() switch
        {
            "gemini" when !Regex.IsMatch(k, @"^(AIza[0-9A-Za-z_\-]{35}|AQ\.[0-9A-Za-z_.\-]{30,})$") =>
                $"Gemini 키는 AIza…(39자) 또는 AQ.…로 시작합니다 — 넣은 값은 {k.Length}자, " +
                $"'{(k.Length >= 4 ? k[..4] : k)}…'로 시작합니다",
            "openrouter" => TranslationOptions.KeyFormatProblem("openrouter", k),
            _ => null,
        };
    }

    public async Task<IReadOnlyList<EngineCheck>> CheckAsync(
        MeaningOptions options, IEnumerable<string> engines, bool saved, CancellationToken ct = default)
    {
        var results = new List<EngineCheck>();
        foreach (var engine in engines.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var check = await CheckOneAsync(options, engine, saved, ct).ConfigureAwait(false);
            _last[engine] = check;
            results.Add(check);
        }
        return results;
    }

    private static async Task<EngineCheck> CheckOneAsync(
        MeaningOptions options, string engine, bool saved, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        var key = engine.ToLowerInvariant() switch
        {
            "gemini" => options.GeminiApiKey,
            "openrouter" => options.OpenRouterApiKey,
            _ => null,
        };
        if (key is { Length: > 0 } && KeyFormatProblem(engine, key) is { } bad)
            return new EngineCheck(engine, false, bad, 0, at, saved);
        if (MeaningWriterRegistry.Build(engine, options.WriterOptions) is not { } writer)
            return new EngineCheck(engine, false, "키가 없습니다", 0, at, saved);

        var watch = Stopwatch.StartNew();
        var result = await writer.WriteAsync("Yesterday", "The Beatles", Probe, options.Lang, ct).ConfigureAwait(false);
        watch.Stop();
        if (result.Text is { } text)
        {
            var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return new EngineCheck(engine, true,
                $"{writer.Model} · \"{(oneLine.Length > 40 ? oneLine[..40] + "…" : oneLine)}\"",
                watch.ElapsedMilliseconds, at, saved);
        }
        return new EngineCheck(engine, false, Explain(result, writer.Model), watch.ElapsedMilliseconds, at, saved);
    }

    /// <summary>흔한 실패를 사람이 바로 손쓸 수 있는 말로. 공급자가 준 이유는 뒤에 붙인다.</summary>
    public static string Explain(MeaningWriteResult r, string model)
    {
        var head = r.StatusCode switch
        {
            400 => "400 — 요청이 거절됐습니다(키 종류·모델 이름 확인)",
            401 => "401 — 키가 틀렸습니다",
            402 => "402 — 결제 잔액이 없습니다(선불 크레딧 소진 · 결제 없는 프로젝트의 키면 무료 티어)",
            403 => "403 — 권한이 없습니다(키 제한·API 사용 설정)",
            404 => $"404 — 모델 {model}을 쓸 수 없습니다(이름·계정 정책 확인)",
            429 => "429 — 한도에 닿았습니다(무료 티어 분·일 한도)",
            { } n => $"HTTP {n}",
            null => r.Reason ?? "응답을 받지 못했습니다",
        };
        return r.StatusCode is not null && r.Reason is { } reason ? $"{head} · {reason}" : head;
    }
}
