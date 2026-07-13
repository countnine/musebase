using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LyricsX.Core.Search;

/// <summary>
/// 다중 제공자 병렬 검색 집계. LyricsKit의 Provider/Group.swift에 해당.
/// 모든 제공자를 동시에 돌리고 결과를 도착 순으로 스트리밍하거나,
/// 품질 순으로 정렬해 반환한다.
/// </summary>
public sealed class LyricsSearchService
{
    private readonly IReadOnlyList<ILyricsProvider> _providers;

    public LyricsSearchService(params ILyricsProvider[] providers)
    {
        _providers = providers.Length > 0
            ? providers
            : [new LrclibProvider(), new NetEaseProvider()];
    }

    /// <summary>모든 제공자 결과를 도착 순으로 스트리밍 (제공자 간 순서 비보장)</summary>
    public async IAsyncEnumerable<Lyrics> SearchAsync(
        LyricsSearchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<Lyrics>();

        var workers = _providers.Select(async provider =>
        {
            try
            {
                await foreach (var lyrics in provider.GetLyricsAsync(request, ct).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(lyrics, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 취소는 조용히
            }
        }).ToArray();

        _ = Task.WhenAll(workers).ContinueWith(
            _ => channel.Writer.TryComplete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var lyrics in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return lyrics;
    }

    /// <summary>전체 결과를 모아 품질 내림차순으로 반환</summary>
    public async Task<List<Lyrics>> SearchAllAsync(LyricsSearchRequest request, CancellationToken ct = default)
    {
        var results = new List<Lyrics>();
        await foreach (var lyrics in SearchAsync(request, ct).ConfigureAwait(false))
            results.Add(lyrics);
        return results.OrderByDescending(l => l.Quality()).ToList();
    }

    /// <summary>최고 품질 후보 1건 (없으면 null)</summary>
    public async Task<Lyrics?> SearchBestAsync(LyricsSearchRequest request, CancellationToken ct = default)
    {
        var all = await SearchAllAsync(request, ct).ConfigureAwait(false);
        return all.FirstOrDefault();
    }
}
