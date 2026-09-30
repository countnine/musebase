using Microsoft.Extensions.Logging;
using Musebase.Core.Translation;

namespace Musebase.Server;

/// <summary>
/// 한 엔진이 막혔다는 사실을 <b>작업이 끝날 때까지</b> 기억한다.
///
/// <see cref="CompositeTranslator"/>는 상태가 없어 매 호출마다 체인 처음부터 돈다 — DeepL이 한도를
/// 넘으면 곡마다 456을 다시 맞는다(500곡이면 헛요청 500번, 그만큼 그 엔진을 계속 두드린다).
/// "언제까지 닫아 둘 것인가"는 작업의 수명을 아는 쪽만 답할 수 있어서 여기(서버)에 둔다.
/// </summary>
public sealed class TranslationChainBreaker
{
    /// <summary>곡의 문제가 아닌 실패는 몇 번까지 봐 주는가(쿼타·인증은 한 번에 닫는다).</summary>
    private const int TransientTolerance = 3;

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _transient = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOpen(string engineId)
    {
        lock (_lock) return _open.ContainsKey(engineId);
    }

    /// <summary>지금 막혀 있는 엔진과 그 이유(화면·로그용).</summary>
    public IReadOnlyDictionary<string, string> Opened
    {
        get { lock (_lock) return new Dictionary<string, string>(_open, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>실패를 기록한다. 닫아야 할 만큼 쌓였으면 <c>true</c>.</summary>
    public bool Record(TranslatorFailure failure)
    {
        lock (_lock)
        {
            if (_open.ContainsKey(failure.EngineId)) return true;

            // 456(한도)·401/403(키)은 다음 곡에서 달라질 이유가 없다 — 한 번에 닫는다.
            if (failure.Kind is TranslatorFailureKind.Quota or TranslatorFailureKind.Auth)
            {
                _open[failure.EngineId] = failure.Kind == TranslatorFailureKind.Quota
                    ? $"한도 초과(HTTP {failure.HttpStatus?.ToString() ?? "?"})"
                    : $"인증 실패(HTTP {failure.HttpStatus?.ToString() ?? "?"})";
                return true;
            }

            // 네트워크·5xx·429는 일시적일 수 있으니 몇 번은 봐 준다.
            var count = _transient.GetValueOrDefault(failure.EngineId) + 1;
            _transient[failure.EngineId] = count;
            if (count < TransientTolerance) return false;

            _open[failure.EngineId] = $"{failure.Kind} 실패가 {count}회 쌓였습니다";
            return true;
        }
    }

    /// <summary>찔러보기에서 이미 죽은 것을 안 엔진은 첫 곡을 돌기 전에 닫아 둔다.</summary>
    public void OpenNow(string engineId, string reason)
    {
        lock (_lock) _open[engineId] = reason;
    }
}

/// <summary>
/// 문자 과금 엔진의 월 무료 한도와 그 달력 키. 작업 시작·자동 번역이 같은 판정을 쓴다.
/// </summary>
public static class TranslationQuota
{
    /// <summary>사용량 미터의 달력 키(UTC 기준 — 서버가 UTC로 돈다).</summary>
    public static string Month() => Month(DateTimeOffset.UtcNow);

    public static string Month(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM");

    /// <summary>
    /// 이 엔진의 월 무료 한도(문자). 모르는 엔진은 null — 한도를 지어내지 않는다.
    /// 둘 다 2026-09 확인: Google은 매월 $10 크레딧(= 50만 자), DeepL Free는 월 50만 자.
    /// DeepL은 넘기면 456으로 스스로 알려 주므로 미터는 사실상 Google 때문에 있다.
    /// </summary>
    public static long? FreeMonthlyChars(string engineId) => engineId.ToLowerInvariant() switch
    {
        "google" => 500_000,
        "deepl" => 500_000,
        _ => null,
    };
}

/// <summary>
/// 일괄 작업 하나가 사는 동안의 번역 상태 — 구성 스냅샷 · 회로 차단 · 엔진별 집계.
///
/// <b>구성을 시작할 때 한 번만 읽는다.</b> 도는 중에 관리 화면에서 엔진을 바꾸면 한 작업 안에서
/// 두 엔진 결과가 섞이고, 비용·품질 판단이 무의미해진다("쓸 때마다 읽는다"의 의도적 예외).
/// </summary>
public sealed class TranslationRun
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Lines, long Chars)> _filled =
        new(StringComparer.OrdinalIgnoreCase);

    public TranslationRun(
        TranslationOptions options, ITranslationCache cache, ILogger logger,
        Action<string, long>? onEngineChars = null)
    {
        Options = options;
        Breaker = new TranslationChainBreaker();

        Service = options.BuildService(cache, new CompositeTranslatorHooks(
            OnFailure: f =>
            {
                if (Breaker.Record(f))
                    logger.LogWarning(
                        "번역 엔진 {Engine}을 이 작업에서 제외합니다 — {Kind}(HTTP {Status})",
                        f.EngineId, f.Kind, f.HttpStatus);
            },
            Skip: Breaker.IsOpen,
            OnFilled: RecordFilled));
        _onEngineChars = onEngineChars;
    }

    private readonly Action<string, long>? _onEngineChars;

    /// <summary>
    /// 체인이 "이 엔진이 몇 줄·몇 자를 채웠다"고 알려 오는 자리.
    /// 문자 과금 엔진의 소프트 미터가 여기 걸려 있다 — Google은 무료 한도를 넘겨도 4xx를 내지
    /// 않으므로 우리가 세지 않으면 넘긴 사실을 알 방법이 없다.
    /// </summary>
    public void RecordFilled(string engine, int lines, int chars)
    {
        lock (_lock)
        {
            var (l, c) = _filled.GetValueOrDefault(engine);
            _filled[engine] = (l + lines, c + chars);
        }
        _onEngineChars?.Invoke(engine, chars);
    }

    /// <summary>
    /// 사용량 미터를 붙여 시작한다. 이번 달 무료 한도를 이미 채운 엔진은 첫 곡 전에 닫고,
    /// <b>도는 중에 한도에 닿아도 그 자리에서 닫는다</b> — 시작할 때만 보면 한 작업 안에서
    /// 50만 자를 넘겨도 계속 Google로 가서 조용히 과금된다(Google은 넘겨도 4xx를 내지 않는다).
    /// </summary>
    public static TranslationRun Begin(TranslationOptions options, LyricsStore store, ILogger logger)
    {
        TranslationRun? run = null;
        run = new TranslationRun(
            options, new StoreTranslationCache(store), logger,
            onEngineChars: (engine, chars) =>
            {
                var total = store.AddUsage(engine, chars, TranslationQuota.Month());
                if (TranslationQuota.FreeMonthlyChars(engine) is { } free && total >= free)
                {
                    run!.Breaker.OpenNow(engine, $"이번 달 무료 한도 {free:N0}자에 닿았습니다");
                    logger.LogWarning(
                        "번역 엔진 {Engine}이 이번 달 무료 한도({Free:N0}자)에 닿아 제외합니다 — 누적 {Total:N0}자",
                        engine, free, total);
                }
            });

        foreach (var id in options.ChainIds)
            if (TranslationQuota.FreeMonthlyChars(id) is { } free
                && store.UsageThisMonth(id, TranslationQuota.Month()) >= free)
                run.Breaker.OpenNow(id, $"이번 달 무료 한도 {free:N0}자를 채웠습니다");

        return run;
    }

    public TranslationOptions Options { get; }
    public TranslationChainBreaker Breaker { get; }
    public LyricsTranslationService Service { get; }

    /// <summary>캐시로 채운 줄(엔진을 안 쓴 줄) — 엔진별 집계가 과소로 보이지 않게 따로 센다.</summary>
    public int CacheHits { get; private set; }

    public void AddCacheHits(int lines)
    {
        lock (_lock) CacheHits += lines;
    }

    /// <summary>진행 화면·요약에 쓸 한 줄. 아무것도 안 했으면 null.</summary>
    public string? Report()
    {
        lock (_lock)
        {
            var parts = _filled
                .OrderByDescending(kv => kv.Value.Lines)
                .Select(kv => $"{kv.Key} {kv.Value.Lines:N0}줄")
                .ToList();
            if (CacheHits > 0) parts.Add($"캐시 {CacheHits:N0}줄");
            foreach (var (engine, reason) in Breaker.Opened) parts.Add($"{engine} 제외({reason})");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}
