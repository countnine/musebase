using System.Collections.Concurrent;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 의미 엔진 체인의 문지기 — 서버 전체가 공유한다(일괄 작업·자동 생성·앱 버튼이 같은 엔진을 쓴다).
///
/// <b>① 한동안 쉬게 한다.</b> Gemini가 402(선불 잔액 소진)를 한 번 주면 다음 곡도 402다 — 곡마다 다시
/// 부르면 매번 한 번씩 헛돈다. 그래서 실패 갈래에 따라 엔진을 잠시 닫는다:
/// 설정 문제(402·401·403·404·400)는 30분, 한도(429)는 10분, 그 밖의 일시적 실패는 3번 연속이면 5분.
/// 설정을 저장하면(<see cref="Reset"/>) 곧바로 다시 연다 — 사람이 고쳤을 테니까.
///
/// <b>② 월 호출 상한.</b> "크레딧 안에서만 Gemini"를 지키는 소프트 미터다. Gemini 무료 티어는 한도에서 429를
/// 주지만, 결제가 붙은 프로젝트는 알려 주지 않고 과금한다(번역의 Google과 같은 사정) — 그래서 서버가 센다.
/// 상한 0은 "세기만 하고 막지 않음"이다.
/// </summary>
public sealed class MeaningEngineGate(LyricsStore store, Func<DateTimeOffset>? clock = null)
{
    /// <summary>미터 이름 앞머리(<c>meaning.usage.{engine}.{YYYY-MM}</c> — 성공한 호출 수).</summary>
    public const string Scope = "meaning";

    public static readonly TimeSpan SetupPause = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RateLimitPause = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FlakyPause = TimeSpan.FromMinutes(5);
    public const int FlakyStrikes = 3;

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Reason)> _closed =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>체인에 넘길 훅 — 월 상한은 지금 구성(<paramref name="options"/>)의 값을 쓴다.</summary>
    public MeaningChainHooks Hooks(MeaningOptions options) => new(
        Skip: engine => Closed(engine, options) is not null,
        OnResult: Record);

    /// <summary>이 엔진을 지금 건너뛰어야 하면 그 이유, 아니면 null.</summary>
    public string? Closed(string engine, MeaningOptions options)
    {
        if (_closed.TryGetValue(engine, out var c))
        {
            if (c.Until > _now()) return $"{c.Reason} — {c.Until:HH:mm} UTC까지 쉼";
            _closed.TryRemove(engine, out _);
        }

        var cap = options.MonthlyCapOf(engine);
        if (cap > 0 && UsedThisMonth(engine) >= cap)
            return $"이번 달 상한 {cap:N0}회에 닿음";
        return null;
    }

    /// <summary>엔진 한 번의 결과를 기록한다(성공이면 미터를 올린다).</summary>
    public void Record(string engine, MeaningWriteResult result)
    {
        if (result.Text is not null)
        {
            _strikes.TryRemove(engine, out _);
            store.AddUsage(engine, 1, TranslationQuota.Month(_now()), Scope);
            return;
        }

        var why = result.StatusCode is { } code ? $"HTTP {code}" : result.Reason ?? "실패";
        if (result.NeedsSetup)
            Close(engine, SetupPause, why + (result.Reason is { } r && result.StatusCode is not null ? $" · {r}" : ""));
        else if (result.StatusCode == 429)
            Close(engine, RateLimitPause, "HTTP 429 · 한도");
        else if (result.Retryable && _strikes.AddOrUpdate(engine, 1, (_, n) => n + 1) >= FlakyStrikes)
            Close(engine, FlakyPause, $"{why} · {FlakyStrikes}번 연속");
    }

    /// <summary>설정이 바뀌었다 — 쉬는 엔진을 모두 깨운다.</summary>
    public void Reset()
    {
        _closed.Clear();
        _strikes.Clear();
    }

    public long UsedThisMonth(string engine) =>
        store.UsageThisMonth(engine, TranslationQuota.Month(_now()), Scope);

    private void Close(string engine, TimeSpan pause, string reason)
    {
        _strikes.TryRemove(engine, out _);
        _closed[engine] = (_now() + pause, reason);
    }
}
