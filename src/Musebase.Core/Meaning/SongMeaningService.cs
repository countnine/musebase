namespace Musebase.Core.Meaning;

/// <summary>한 곡에 대한 의미 생성 결과.</summary>
/// <param name="Status">`ok` | `no-source` | `failed`.</param>
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
    /// <summary>자료는 있었지만 생성이 실패했다(키·쿼타·네트워크).</summary>
    public const string Failed = "failed";

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

    public async Task<SongMeaning> BuildAsync(
        string title, string artist, string targetLang, CancellationToken ct = default)
    {
        var collected = await CollectAsync(title, artist, ct).ConfigureAwait(false);
        if (collected.Count == 0)
            return new SongMeaning(SongMeaning.NoSource, null, collected, _writer?.EngineId, _writer?.Model);

        if (_writer is null)
            return new SongMeaning(SongMeaning.Failed, null, collected, null, null);

        var summary = await _writer.WriteAsync(title, artist, collected, targetLang, ct).ConfigureAwait(false);
        return summary is null
            ? new SongMeaning(SongMeaning.Failed, null, collected, _writer.EngineId, _writer.Model)
            : new SongMeaning(SongMeaning.Ok, summary, collected, _writer.EngineId, _writer.Model);
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
