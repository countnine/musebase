using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 곡의 의미를 실제로 만들어 저장하는 한 곳.
///
/// 관리자 화면과 앱용 <c>POST /v1/meaning</c>이 <b>같은 코드</b>를 쓴다 — 예전에는 이 절차가
/// 관리자 엔드포인트 안의 지역 함수였는데, 호출자가 둘이 되면서 Musixmatch 주소 조회와
/// 저장 규칙이 갈라질 위험이 생겼다.
///
/// <b>같은 곡을 동시에 두 번 만들지 않는다.</b> 앱 버튼은 여러 기기에서 동시에 눌릴 수 있고
/// 생성 한 번은 외부 API 여러 개 + LLM 호출이라 비싸다. 진행 중인 것이 있으면 그 결과를 같이 받는다.
///
/// <b>만들지 못한 이유는 반드시 로그에 남긴다.</b> 예전에는 공급자의 402(잔액)·404(모델 정책)가
/// 흔적 없이 "일시적 오류"·"실패"로만 보여, 원인을 알려면 서버에서 curl로 재현해야 했다.
/// </summary>
public sealed class MeaningGenerator(LyricsStore store, MeaningSettings settings, ILogger<MeaningGenerator> logger)
{
    // 구성은 관리 화면에서 바뀔 수 있다 — 붙잡아 두지 말고 쓸 때마다 읽는다.
    private SongMeaningService Service => settings.Service;
    private MeaningOptions Options => settings.Current;

    private readonly ConcurrentDictionary<string, Task<MeaningOutcome>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>엔진과 자료원이 갖춰져 실제로 만들 수 있는가.</summary>
    public bool IsEnabled => Service.IsEnabled;

    /// <summary>지금 켜져 있는 자료원 이름(화면 표시용).</summary>
    public IReadOnlyList<string> SourceNames => Service.SourceNames;

    /// <summary>
    /// 결과를 저장하고 status와 (실패했다면) 그 이유를 돌려준다.
    ///
    /// 실패·자료없음도 행으로 남겨 백필이 같은 곡을 무한히 재시도하지 않게 한다 —
    /// <b>단 일시적 실패(<see cref="SongMeaning.Retry"/>)와 설정 문제(<see cref="SongMeaning.Config"/>)는
    /// 예외다.</b> 곡의 문제가 아니므로 행으로 굳히면 원인이 풀린 뒤에도 그 곡은 영영 건너뛰어진다.
    /// </summary>
    /// <param name="only">이번 한 번만 쓸 자료원(관리자 화면의 체크박스). 비우면 설정값을 쓴다.</param>
    public Task<MeaningOutcome> GenerateAsync(
        string key, string title, string artist, IReadOnlyList<string>? only = null)
    {
        // 자료원을 따로 고른 요청은 "같은 작업"이 아니다 — 합치면 엉뚱한 조합의 결과를 받는다.
        var gateKey = only is { Count: > 0 } ? $"{key}|>{string.Join(",", only)}" : key;

        var task = _inFlight.GetOrAdd(gateKey, _ => RunAsync(key, title, artist, only));
        // 끝나면 자리를 비워 다음 [다시 생성]이 새로 돌게 한다.
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(gateKey, out Task<MeaningOutcome>? _), TaskScheduler.Default);
        return task;
    }

    private async Task<MeaningOutcome> RunAsync(
        string key, string title, string artist, IReadOnlyList<string>? only)
    {
        var service = only is { Count: > 0 } ? Options.BuildService(only) : Service;

        var result = await service.BuildAsync(title, artist, Options.Lang).ConfigureAwait(false);
        var outcome = new MeaningOutcome(result.Status, result.Detail);

        // 자료 없음·자료 부족은 흔한 정상 결과다 — 로그는 엔진이 실패했을 때만 남긴다.
        if (result.Status is SongMeaning.Failed or SongMeaning.Retry or SongMeaning.Config)
            logger.LogWarning(
                "의미 생성 {Status}: {Artist} - {Title} · {Engine}/{Model} · {Detail}",
                result.Status, artist, title, result.Engine, result.Model, result.Detail ?? "(사유 없음)");

        if (SongMeaning.IsUnsaved(result.Status)) return outcome;

        // 곡 페이지 주소는 의미 자료원과 별개다 — Musixmatch를 자료로 쓰지 않아도 링크는 정확해야 한다.
        var musixmatch = await Options.MusixmatchApi().FindAsync(title, artist).ConfigureAwait(false);

        store.UpsertMeaning(
            MeaningMapper.ToEntry(key, title, artist, Options.Lang, result)
            with { MusixmatchUrl = musixmatch?.ShareUrl });
        return outcome;
    }
}

/// <summary>생성 한 번의 결과 — 상태와, 만들지 못했다면 공급자가 준 이유(<c>HTTP 402 · …</c>).</summary>
public sealed record MeaningOutcome(string Status, string? Detail);
