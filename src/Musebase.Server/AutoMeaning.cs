using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 기기가 올린 새 곡의 의미를 서버가 곧바로 만든다 — 가사 번역(<see cref="AutoTranslator"/>)과 같은 흐름.
///
/// 예전에는 "생성은 사람이 누를 때만"이었다. 엔진 체인이 무료(Gemini 무료 티어·크레딧) → 싼 엔진
/// (OpenRouter flash-lite, 곡당 1원 미만) 순으로 서게 되면서 비용 걱정이 번역 수준으로 내려왔고,
/// 곡마다 사람이 누르기엔 너무 잦다. 대신 가드를 둔다: ① 엔진·자료원이 갖춰졌을 때만 ② 자동 생성
/// 월 상한(곡 수) ③ 엔진별 월 호출 상한과 쉬게 하기(<see cref="MeaningEngineGate"/>) ④ 관리 화면에서 끌 수 있다.
///
/// 이미 행이 있는 곡(자료없음·실패 포함)은 다시 만들지 않는다 — 다시 만드는 것은 사람의 몫이다.
/// 저장되지 않은 결과(잠시 후 다시·설정 문제)는 다음 업로드 때 다시 큐에 들어온다.
/// </summary>
public sealed class AutoMeaning
{
    private const int QueueCapacity = 2000;

    /// <summary>미터 이름 — 자동 생성으로 엔진을 부른 곡 수.</summary>
    public const string MeterName = "auto";

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);

    private readonly LyricsStore _store;
    private readonly MeaningSettings _settings;
    private readonly MeaningGenerator _generator;
    private readonly ILogger<AutoMeaning> _logger;
    private string? _capNotedMonth;

    public AutoMeaning(LyricsStore store, MeaningSettings settings, MeaningGenerator generator, ILogger<AutoMeaning> logger)
    {
        _store = store;
        _settings = settings;
        _generator = generator;
        _logger = logger;
    }

    public bool Enabled => _settings.Current.AutoGenerate && _generator.IsEnabled;

    public int Pending => _queued.Count;

    public string? LastReport { get; private set; }

    /// <summary>이 곡의 의미가 곧 생기는가(큐에 있거나 만드는 중) — 앱 조회에 "잠시 후 다시"를 알린다.</summary>
    public bool IsPending(string key) => _queued.ContainsKey(key) || _generator.IsGenerating(key);

    /// <summary>방금 저장된 곡을 큐에 넣는다. 꺼져 있거나 이미 의미 행이 있으면 아무것도 안 한다.</summary>
    public bool Offer(string? key)
    {
        if (string.IsNullOrEmpty(key) || !Enabled) return false;
        if (_store.GetMeaningByKey(key) is not null) return false;
        if (!_queued.TryAdd(key, 0)) return false;
        if (_queue.Writer.TryWrite(key)) return true;
        _queued.TryRemove(key, out _);
        return false;
    }

    public async Task RunAsync(CancellationToken stop)
    {
        int done = 0, ok = 0;
        while (!stop.IsCancellationRequested)
        {
            if (!_queue.Reader.TryRead(out var key))
            {
                if (done > 0)
                {
                    LastReport = $"{DateTimeOffset.UtcNow:MM-dd HH:mm} UTC · 만든 곡 {ok}/{done}";
                    _logger.LogInformation("자동 의미: {Report}", LastReport);
                    done = ok = 0;
                }
                try { key = await _queue.Reader.ReadAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            try
            {
                if (!Enabled) continue;
                var options = _settings.Current;
                var month = TranslationQuota.Month();
                if (_store.UsageThisMonth(MeterName, month, MeaningEngineGate.Scope) >= options.AutoMonthlyCap)
                {
                    if (_capNotedMonth != month)
                    {
                        _capNotedMonth = month;
                        _logger.LogWarning("자동 의미 생성 월 상한({Cap}곡)에 닿아 이번 달은 멈춥니다", options.AutoMonthlyCap);
                    }
                    continue;
                }

                if (_store.GetMeaningByKey(key) is not null) continue;      // 그새 누가 만들었다
                if (_store.GetByKey(key) is not { } entry) continue;        // 그새 지워졌다
                if (_store.GetMeaning(entry.Title, entry.Artist) is not null) continue; // 표기가 갈린 형제 행에 이미 있다

                var outcome = await _generator.GenerateAsync(key, entry.Title, entry.Artist).ConfigureAwait(false);
                done++;
                if (outcome.Status == SongMeaning.Ok) ok++;
                // 엔진을 실제로 부른 경우만 센다(자료 없음은 LLM을 부르지 않는다).
                if (outcome.Status is SongMeaning.Ok or SongMeaning.Insufficient or SongMeaning.Failed)
                    _store.AddUsage(MeterName, 1, month, MeaningEngineGate.Scope);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                done++;
                _logger.LogWarning(e, "자동 의미 생성 실패: {Key}", key);
            }
            finally
            {
                // 만드는 동안에도 "곧 생긴다"로 보이게, 끝난 뒤에 자리를 비운다.
                _queued.TryRemove(key, out _);
            }

            if (_settings.Current.BackfillDelayMs > 0)
            {
                try { await Task.Delay(_settings.Current.BackfillDelayMs, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
