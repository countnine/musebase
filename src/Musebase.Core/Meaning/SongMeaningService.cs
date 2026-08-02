namespace Musebase.Core.Meaning;

/// <summary>한 곡에 대한 의미 생성 결과.</summary>
/// <param name="Status">`ok` | `no-source` | `failed` | `retry`.</param>
/// <param name="Summary">생성된 대상 언어 문단. `ok`가 아니면 null.</param>
/// <param name="Sources">근거로 쓴 원문들(출처 표기·재생성 판단용).</param>
public sealed record SongMeaning(
    string Status,
    string? Summary,
    IReadOnlyList<MeaningSource> Sources,
    string? Engine,
    string? Model)
{
    public const string Ok = "ok";
    /// <summary>어느 소스에도 자료가 없었다 — LLM은 부르지 않았다.</summary>
    public const string NoSource = "no-source";
    /// <summary>자료는 있었지만 생성이 영구적으로 실패했다(키가 틀렸다, 응답이 비었다).</summary>
    public const string Failed = "failed";

    /// <summary>
    /// 일시적 실패(쿼타·서버·네트워크) — **저장하지 않는다.** 저장하면 쿼타가 풀린 뒤에도
    /// 백필이 이 곡을 영영 건너뛴다. 이 상태는 DB에 들어가지 않는 값이다.
    /// </summary>
    public const string Retry = "retry";

    public string? GeniusUrl =>
        Sources.FirstOrDefault(s => s.Name == "Genius")?.Url;
}

/// <summary>
/// 소스 수집 → 요약을 한 번에 수행한다.
///
/// 두 가지가 설계의 핵심이다.
/// 1. **소스는 병렬로, 실패는 무시.** 하나가 죽어도 나머지가 채운다.
/// 2. **자료가 하나도 없으면 LLM을 부르지 않는다.** 곡 해설은 그럴듯한 창작이 특히 쉬운
///    영역이라, 근거 없이 부르면 모델이 지어낸다. 토큰 낭비이기도 하다.
/// </summary>
public sealed class SongMeaningService
{
    private readonly IReadOnlyList<ISongMeaningSource> _sources;
    private readonly IMeaningWriter? _writer;

    public SongMeaningService(IReadOnlyList<ISongMeaningSource> sources, IMeaningWriter? writer)
    {
        _sources = sources;
        _writer = writer;
    }

    /// <summary>소스도 엔진도 구성되지 않았으면 이 기능은 꺼진 것이다.</summary>
    public bool IsEnabled => _writer is not null && _sources.Count > 0;

    /// <summary>켜져 있는 소스 이름들 — 무엇에 근거해 만들어지는지 화면에 보여 주기 위한 것.</summary>
    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToList();

    public async Task<SongMeaning> BuildAsync(
        string title, string artist, string targetLang, CancellationToken ct = default)
    {
        var collected = await CollectAsync(title, artist, ct).ConfigureAwait(false);
        if (collected.Count == 0)
            return new SongMeaning(SongMeaning.NoSource, null, collected, _writer?.EngineId, _writer?.Model);

        if (_writer is null)
            return new SongMeaning(SongMeaning.Failed, null, collected, null, null);

        var written = await _writer.WriteAsync(title, artist, collected, targetLang, ct).ConfigureAwait(false);
        if (written.Text is not null)
            return new SongMeaning(SongMeaning.Ok, written.Text, collected, _writer.EngineId, _writer.Model);

        // 쿼타·네트워크처럼 시간이 풀어 줄 실패는 `failed`로 굳히지 않는다.
        var status = written.Retryable ? SongMeaning.Retry : SongMeaning.Failed;
        return new SongMeaning(status, null, collected, _writer.EngineId, _writer.Model);
    }

    /// <summary>모든 소스를 동시에 부르고 성공한 것만 모은다(레지스트리 등록 순서 유지).</summary>
    public async Task<IReadOnlyList<MeaningSource>> CollectAsync(
        string title, string artist, CancellationToken ct = default)
    {
        var tasks = _sources.Select(s => s.FetchAsync(title, artist, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }
}
