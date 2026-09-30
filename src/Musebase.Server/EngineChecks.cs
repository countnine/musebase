using System.Collections.Concurrent;
using System.Diagnostics;
using Musebase.Core.Translation;

namespace Musebase.Server;

/// <summary>엔진 하나를 확인한 결과(형식 검사 + 실제 호출).</summary>
/// <param name="Saved">저장된 설정으로 확인했는가(false면 "저장하지 않고 테스트"의 결과다).</param>
public sealed record EngineCheck(
    string Engine, bool Ok, string? Detail, long Millis, DateTimeOffset At, bool Saved);

/// <summary>
/// 번역 엔진 키가 실제로 맞는지 화면에서 확인한다 — <b>형식 → 실제 호출</b> 순서다.
///
/// 형식은 호출 없이 공짜로 가를 수 있어 먼저 본다(Google 키 칸에 엉뚱한 자격증명이 들어가
/// 6곡이 조용히 건너뛰어진 사고가 있었다). 형식이 맞으면 짧은 두 줄을 실제로 번역해 본다 —
/// 키가 살아 있는지, 결제·권한이 켜져 있는지는 호출해 봐야만 안다. 비용은 글자 몇 개다.
///
/// 결과는 메모리에만 둔다(재시작하면 "확인 안 함"으로 돌아간다) — 키의 상태는 바깥 사정으로
/// 언제든 바뀌므로 오래 묵은 ✓를 남겨 두는 편이 오히려 해롭다.
/// </summary>
public sealed class EngineCheckBook
{
    private readonly ConcurrentDictionary<string, EngineCheck> _last = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, EngineCheck> Last => _last;

    /// <summary>엔진들을 차례로 확인하고 결과를 기록한다.</summary>
    public async Task<IReadOnlyList<EngineCheck>> CheckAsync(
        TranslationOptions options, IEnumerable<string> engines, bool saved, CancellationToken ct = default)
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
        TranslationOptions options, string engine, bool saved, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;

        // ① 형식 — 호출 전에 공짜로 거른다.
        if (options.KeyFor(engine) is { Length: > 0 } key
            && TranslationOptions.KeyFormatProblem(engine, key) is { } bad)
            return new EngineCheck(engine, false, bad, 0, at, saved);

        // ② 만들 수 있는가 — 키가 필요한데 비었으면 여기서 null이 나온다.
        if (options.BuildOne(engine) is not { } translator)
            return new EngineCheck(engine, false, "키가 없습니다", 0, at, saved);

        // ③ 실제 호출. 한 줄은 대표성이 없다(LLM은 1줄에 중첩 배열을 돌려주는 일이 있다).
        var watch = Stopwatch.StartNew();
        try
        {
            var got = await translator.TranslateAsync(["hello", "goodbye"], options.Lang, ct)
                .ConfigureAwait(false);
            watch.Stop();
            var sample = got.FirstOrDefault(g => g is { Length: > 0 });
            return sample is null
                // 예외 없이 빈 결과 = 엔진은 살아 있고 이 입력에서 줄 계약만 못 지켰다.
                ? new EngineCheck(engine, true, "응답은 왔지만 줄 수를 지키지 않았습니다(모델 확인)",
                    watch.ElapsedMilliseconds, at, saved)
                : new EngineCheck(engine, true, $"\"hello\" → \"{sample}\"", watch.ElapsedMilliseconds, at, saved);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            watch.Stop();
            return new EngineCheck(engine, false, Explain(e), watch.ElapsedMilliseconds, at, saved);
        }
    }

    /// <summary>흔한 실패를 사람이 바로 손쓸 수 있는 말로 바꾼다.</summary>
    private static string Explain(Exception e) => e is HttpRequestException { StatusCode: { } code }
        ? (int)code switch
        {
            400 => "400 — 요청이 거절됐습니다(키 종류가 맞지 않거나 API가 꺼져 있을 수 있습니다)",
            401 => "401 — 키가 틀렸거나 이 API용 키가 아닙니다",
            402 => "402 — 잔액이 부족합니다(충전이 필요합니다)",
            403 => "403 — 권한이 없습니다(키 제한사항·API 사용 설정을 확인하세요)",
            429 => "429 — 요청이 너무 많습니다(잠시 후 다시)",
            456 => "456 — 이번 달 한도를 다 썼습니다",
            var n => $"HTTP {n}",
        }
        : e is TimeoutException ? "응답이 늦어 끊었습니다(타임아웃)" : e.Message;
}
