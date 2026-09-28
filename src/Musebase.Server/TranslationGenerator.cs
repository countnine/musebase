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

    /// <summary>한 곡을 번역해 저장한다. 같은 곡·같은 언어 요청이 겹치면 결과를 함께 받는다.</summary>
    public Task<TranslationOutcome> TranslateAsync(string key, string targetLang, CancellationToken ct = default)
    {
        var gate = $"{key}|{targetLang}";
        var task = _inFlight.GetOrAdd(gate, _ => RunAsync(key, targetLang, ct));
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(gate, out Task<TranslationOutcome>? _), TaskScheduler.Default);
        return task;
    }

    private async Task<TranslationOutcome> RunAsync(string key, string targetLang, CancellationToken ct)
    {
        var entry = store.GetByKey(key);
        if (entry is null) return new TranslationOutcome("failed", 0, 0, "곡을 찾지 못했습니다");

        if (Lyrics.Parse(entry.Lrc) is not { } lyrics)
            return new TranslationOutcome("unparsable", 0, 0, "LRC를 읽지 못했습니다");

        var expected = Estimate(lyrics, targetLang);
        var changed = await settings.Service
            .EnsureTranslatedAsync(lyrics, targetLang, ct, null)
            .ConfigureAwait(false);

        // 아무것도 늘지 않았으면 손대지 않는다 — 원본을 재직렬화할 이유가 없다.
        if (changed == 0) return new TranslationOutcome("skipped", 0, 0);

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
