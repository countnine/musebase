using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Musebase.Server;

/// <summary>
/// 기기가 올린 새 가사를 서버가 곧바로 번역한다.
///
/// <b>왜 필요한가.</b> 기기가 번역을 하지 않게 되면(새 설치의 기본값을 "서버에 맡김"으로 바꿀 예정이다)
/// 처음 틀어 본 곡은 관리자가 일괄 작업을 누를 때까지 번역 없이 남는다. 이 워커가 그 틈을 메운다 —
/// 업로드 → 큐 → 한 곡씩 번역 → 저장. 다음 기기부터는 서버에서 번역이 붙은 채로 받는다.
///
/// <b>"생성은 사람이 누를 때만"이라는 원칙을 깬다</b>(의미 생성도 뒤따라 같은 흐름이 됐다 — <see cref="AutoMeaning"/>).
/// 번역은 비용이 몇 자릿수 작고(월 31만 자가 무료 한도 안이다) 곡마다 사람이 누르기엔
/// 너무 잦다. 대신 가드를 둔다: ① 엔진이 구성돼 있을 때만 ② 자동 번역 월 문자 상한
/// ③ 무료 한도에 닿은 엔진은 미터가 닫는다 ④ 공개 엔진으로는 체인 규칙상 절대 안 간다
/// ⑤ 관리 화면에서 끌 수 있다.
///
/// 실패는 조용히 넘긴다 — 그 곡은 다음 일괄 작업이 주워 간다. 한 번에 한 곡만, 곡 사이 간격을 둔다.
/// </summary>
public sealed class AutoTranslator
{
    /// <summary>큐 상한. 넘치면 새 요청을 버린다(일괄 작업이 나중에 주워 간다).</summary>
    private const int QueueCapacity = 2000;

    /// <summary>미터에 쓰는 가상 엔진 이름 — 자동 번역이 쓴 문자를 따로 센다.</summary>
    public const string MeterName = "auto";

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    // 같은 곡이 큐에 두 번 들어가지 않게 — 기기 둘이 같은 곡을 연달아 올리는 일이 흔하다.
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);

    private readonly LyricsStore _store;
    private readonly TranslationSettings _settings;
    private readonly TranslationGenerator _generator;
    private readonly ILogger<AutoTranslator> _logger;

    private string? _capNotedMonth;

    public AutoTranslator(
        LyricsStore store, TranslationSettings settings, TranslationGenerator generator,
        ILogger<AutoTranslator> logger)
    {
        _store = store;
        _settings = settings;
        _generator = generator;
        _logger = logger;
    }

    /// <summary>지금 돌 수 있는가(켜져 있고 엔진이 있다).</summary>
    public bool Enabled => _settings.Current.AutoTranslate && _settings.IsEnabled;

    /// <summary>대기 중인 곡 수(화면 표시용).</summary>
    public int Pending => _queued.Count;

    /// <summary>마지막으로 한 무더기를 처리한 요약(화면 표시용). 아직 없으면 null.</summary>
    public string? LastReport { get; private set; }

    /// <summary>
    /// 방금 저장된 곡을 큐에 넣는다. 대상 언어 번역이 이미 있거나 꺼져 있으면 아무것도 안 한다.
    /// </summary>
    /// <returns>큐에 넣었으면 true.</returns>
    public bool Offer(string? key, IReadOnlyList<string>? langs)
    {
        if (string.IsNullOrEmpty(key) || !Enabled) return false;

        // 기기가 이미 번역해 올렸으면 할 일이 없다(PC가 DeepL로 번역해 올리는 경우).
        var target = _settings.Current.Lang.ToLowerInvariant();
        if (langs is not null && langs.Any(l => string.Equals(l, target, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!_queued.TryAdd(key, 0)) return false;
        if (_queue.Writer.TryWrite(key)) return true;

        _queued.TryRemove(key, out _);   // 큐가 가득 찼다 — 일괄 작업이 나중에 주워 간다
        return false;
    }

    /// <summary>서버가 사는 동안 도는 루프. 곡 하나에서 무슨 일이 나도 루프는 죽지 않는다.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        TranslationRun? run = null;
        int done = 0, ok = 0;

        while (!stop.IsCancellationRequested)
        {
            string key;
            // 큐가 비면 이번 무더기를 정리한다 — 회로 차단은 무더기 단위로 산다. 한 번의 일시적
            // 실패로 엔진을 영영 닫아 두지 않도록, 다음 무더기는 새로 시작한다.
            if (!_queue.Reader.TryRead(out key!))
            {
                if (run is not null)
                {
                    LastReport = $"{DateTimeOffset.UtcNow:MM-dd HH:mm} UTC · {ok}/{done}곡"
                                 + (run.Report() is { } note ? $" · {note}" : "");
                    _logger.LogInformation("자동 번역: {Report}", LastReport);
                    run = null;
                    done = ok = 0;
                }

                try
                {
                    key = await _queue.Reader.ReadAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            _queued.TryRemove(key, out _);
            if (!Enabled) continue;

            var options = _settings.Current;
            var month = TranslationQuota.Month();

            // 자동 번역만의 월 상한 — 사람이 누르지 않는 경로라 따로 묶는다.
            var used = _store.UsageThisMonth(MeterName, month);
            if (used >= options.AutoMonthlyCap)
            {
                if (_capNotedMonth != month)
                {
                    _capNotedMonth = month;
                    _logger.LogWarning(
                        "자동 번역 월 상한({Cap:N0}자)에 닿아 이번 달은 멈춥니다 — 남은 곡은 일괄 작업으로 돌리세요",
                        options.AutoMonthlyCap);
                }
                continue;
            }

            // 구성이 바뀌었으면(관리 화면에서 엔진을 바꿨다) 새 체인으로 다시 시작한다.
            if (run is null || !ReferenceEquals(run.Options, options))
                run = TranslationRun.Begin(options, _store, _logger);

            try
            {
                var outcome = await _generator
                    .TranslateAsync(key, options.Lang, stop, run).ConfigureAwait(false);
                done++;
                if (outcome.Status == "ok")
                {
                    ok++;
                    _store.AddUsage(MeterName, outcome.Chars, month);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                done++;
                _logger.LogWarning(e, "자동 번역 실패: {Key}", key);
            }

            if (options.DelayMs > 0)
            {
                try { await Task.Delay(options.DelayMs, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
