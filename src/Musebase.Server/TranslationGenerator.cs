using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Server;

/// <summary>번역 한 곡의 결과.</summary>
/// <param name="Status">
/// <c>ok</c> 저장함 · <c>skipped</c> 할 일 없음 · <c>unparsable</c> LRC를 읽지 못함 ·
/// <c>lossy</c> 재직렬화가 원본을 깎아 저장을 거부함 · <c>rejected</c> 병합 정책이 거절함 ·
/// <c>failed</c> 그 밖의 실패.
/// </param>
/// <param name="Chars">이번에 실제로 번역기에 보낸 문자 수(캐시로 채운 줄은 빼고).</param>
public sealed record TranslationOutcome(string Status, int ChangedLines, long Chars, string? Detail = null);

/// <summary>엔진 하나를 찔러 본 결과.</summary>
public sealed record ProbeResult(string EngineId, bool Ok, string? Detail);

/// <summary>
/// 가사 한 곡을 번역해 저장하는 한 곳. <see cref="MeaningGenerator"/>의 형제다 —
/// 같은 곡을 동시에 두 번 번역하지 않도록 같은 방식의 게이트를 둔다.
///
/// <b>여기가 서버에서 LRC를 다시 쓰는 유일한 자동 경로다.</b> 저장소는 원본 문자열을 그대로
/// 보관하는 것을 원칙으로 하는데(<see cref="LyricsStore"/>), 번역을 채우려면 파싱 →
/// 변형 → 재직렬화가 불가피하다. 그래서 두 겹으로 막는다.
/// ① 번역이 실제로 늘지 않으면(<c>changed == 0</c>) <b>저장 자체를 하지 않는다</b> —
///    재직렬화만 하고 revision을 올리는 최악을 원천 차단한다.
/// ② 재직렬화 결과가 원본보다 <b>깎였으면</b>(줄 수 감소·글자단위 타임태그 소실) 저장을 거부하고
///    <c>lossy</c>로 센다. 병합 정책이 어차피 막지만, 사유가 <c>poorer-content</c>로만 남으면
///    무엇이 깨졌는지 알 수 없다.
/// </summary>
public sealed class TranslationGenerator(
    LyricsStore store, ITranslationServiceSource settings, ITranslationCache cache,
    ILogger<TranslationGenerator> logger)
{
    private readonly ConcurrentDictionary<string, Task<TranslationOutcome>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>엔진과 키가 갖춰져 실제로 번역할 수 있는가.</summary>
    public bool IsEnabled => settings.IsEnabled;

    /// <summary>
    /// 이 곡을 번역하면 <b>몇 글자를 보내게 되는지</b>. 확인 화면의 추정과 실행 중 예산 차감이
    /// 같은 함수를 쓰므로 둘이 어긋날 수 없다.
    ///
    /// 세는 규칙은 <see cref="LyricsTranslationService.EnsureTranslatedAsync"/>의 1단계와 같다:
    /// 빈 줄 제외 → 이미 대상 언어 태그가 있는 줄 제외 → <b>중복 원문은 한 번만</b> →
    /// 캐시에 이미 있는 줄 제외.
    /// </summary>
    public long Estimate(string key, string targetLang)
    {
        var entry = store.GetByKey(key);
        if (entry is null || Lyrics.Parse(entry.Lrc) is not { } lyrics) return 0;
        return Estimate(lyrics, targetLang);
    }

    /// <summary>이미 파싱해 둔 가사에 대한 추정(같은 곡을 두 번 읽지 않으려고).</summary>
    private long Estimate(Lyrics lyrics, string targetLang)
    {
        var tag = LineAttachments.TranslationTag(targetLang.ToLowerInvariant());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long chars = 0;

        foreach (var line in lyrics.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Content)) continue;
            if (line.Attachments[tag] is not null) continue;
            if (!seen.Add(line.Content)) continue;
            if (cache.Get(line.Content, targetLang) is not null) continue;
            chars += line.Content.Length;
        }

        return chars;
    }

    /// <summary>
    /// 체인 구성원 <b>각각</b>이 실제로 대답하는지 시작 전에 확인한다.
    ///
    /// <see cref="LyricsTranslationService"/>는 실패를 조용히 삼켜 "바뀐 줄 0"으로 돌려준다
    /// (재생 중에 오류창을 띄우지 않으려는 규칙이다). 그대로 두면 키가 틀렸을 때 일괄 작업이
    /// <b>"건너뜀 N곡"</b>으로만 끝나 원인을 알 길이 없다 — 실제로 Google 키 자리에 다른 자격증명이
    /// 들어가 6곡이 통째로 조용히 건너뛰어졌다. 그래서 여기서만 번역기를 직접 부른다.
    ///
    /// <b>체인을 통째로 찌르면 안 된다</b> — 보조가 살아 있으면 주 엔진의 죽음이 가려져
    /// 이 함수가 만들어진 이유가 통째로 사라진다. 그래서 멤버를 하나씩 확인한다.
    /// </summary>
    public async Task<IReadOnlyList<ProbeResult>> ProbeChainAsync(
        string targetLang, CancellationToken ct = default)
    {
        var members = settings.Members;
        if (members.Count == 0)
            return [new ProbeResult("none", false, "번역 엔진이 구성되지 않았습니다.")];

        var results = new List<ProbeResult>();
        foreach (var (engineId, translator) in members)
        {
            try
            {
                // 두 줄을 보낸다. 한 줄짜리는 대표성이 없다 — LLM은 1줄 입력에 `[["안녕"]]`처럼
                // 중첩 배열을 돌려주는 일이 있어(실측) 멀쩡한 엔진이 죽은 것으로 보인다.
                var probe = await translator
                    .TranslateAsync(["hello", "goodbye"], targetLang, ct).ConfigureAwait(false);

                // **예외가 없으면 살아 있는 것이다.** 키가 틀리면 이제 상태 코드를 실은 예외가
                // 올라오므로(비2xx를 삼키지 않는다), 여기서 빈 결과는 "엔진이 죽었다"가 아니라
                // "이 입력에서 줄 계약을 못 지켰다"는 뜻이다 — 그걸로 작업을 막으면 안 된다.
                var filled = probe.Any(p => p is { Length: > 0 });
                results.Add(new ProbeResult(engineId, true,
                    filled ? null : "응답은 왔지만 줄 수를 지키지 않았습니다(모델을 확인하세요)"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // HttpRequestException은 상태 코드를 메시지에 담는다("… 401 (Unauthorized).").
                results.Add(new ProbeResult(engineId, false, e.Message));
            }
        }
        return results;
    }

    /// <summary>한 곡을 번역해 저장한다. 같은 곡·같은 언어 요청이 겹치면 결과를 함께 받는다.</summary>
    public Task<TranslationOutcome> TranslateAsync(
        string key, string targetLang, CancellationToken ct = default, TranslationRun? run = null)
    {
        var gate = $"{key}|{targetLang}";
        var task = _inFlight.GetOrAdd(gate, _ => RunAsync(key, targetLang, ct, run));
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(gate, out Task<TranslationOutcome>? _), TaskScheduler.Default);
        return task;
    }

    private async Task<TranslationOutcome> RunAsync(
        string key, string targetLang, CancellationToken ct, TranslationRun? run = null)
    {
        var entry = store.GetByKey(key);
        if (entry is null) return new TranslationOutcome("failed", 0, 0, "곡을 찾지 못했습니다");

        if (Lyrics.Parse(entry.Lrc) is not { } lyrics)
            return new TranslationOutcome("unparsable", 0, 0, "LRC를 읽지 못했습니다");

        var expected = Estimate(lyrics, targetLang);
        var stats = new TranslationRunStats();
        // 작업이 있으면 그 작업의 서비스를 쓴다 — 회로 차단과 엔진별 집계가 거기 붙어 있다.
        var changed = await (run?.Service ?? settings.Service)
            .EnsureTranslatedAsync(lyrics, targetLang, ct, stats)
            .ConfigureAwait(false);
        run?.AddCacheHits(stats.CacheHits);

        // 아무것도 늘지 않았으면 손대지 않는다 — 원본을 재직렬화할 이유가 없다.
        // 단 **보낼 것이 있었는데도** 0이면 그것은 "할 일이 없었다"가 아니라 번역기가 실패한 것이다
        // (서비스가 실패를 조용히 삼킨다). 그 둘을 같은 칸에 세면 키가 틀려도 "건너뜀"으로만 보인다.
        if (changed == 0)
            return expected > 0
                ? new TranslationOutcome("failed", 0, 0,
                    "번역기가 한 줄도 돌려주지 않았습니다 — 엔진과 키를 확인하세요")
                : new TranslationOutcome("skipped", 0, 0);

        var rewritten = lyrics.ToString();
        var before = LyricsFacts.From(entry.Lrc);
        var after = LyricsFacts.From(rewritten);

        // 재직렬화가 원본을 깎았다 — 저장하지 않고 그 형태를 로그에 남긴다(다음에 제외할 근거).
        if (after.LineCount < before.LineCount || (before.HasInlineTimeTags && !after.HasInlineTimeTags))
        {
            logger.LogWarning(
                "번역 저장 거부(형식 보존 불가): {Artist} - {Title} · 줄 {Before}→{After} · 인라인 {InlineBefore}→{InlineAfter}",
                entry.Artist, entry.Title, before.LineCount, after.LineCount,
                before.HasInlineTimeTags, after.HasInlineTimeTags);
            return new TranslationOutcome("lossy", changed, expected, "재직렬화가 원본을 깎아 저장하지 않았습니다");
        }

        // Origin은 반드시 provider다 — user로 쓰면 이후 기기 업로드가 전부
        // RejectUserEditProtected로 막힌다(사람이 고친 가사를 지키는 규칙이 잘못 걸린다).
        var saved = store.Upsert(
            entry with { Lrc = rewritten, Origin = LyricsEntry.OriginProvider },
            updatedBy: "server-translate", out var rejection);

        if (saved is null)
            return new TranslationOutcome("rejected", changed, expected, rejection?.Reason ?? "저장이 거절됐습니다");

        return new TranslationOutcome("ok", changed, expected);
    }
}
