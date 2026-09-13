namespace Musebase.Server;

/// <summary>
/// 곡에 <b>딸린 것들</b> — 커버 이미지와 Last.fm 좋아요.
///
/// 관리자 화면과 앱용 <c>/v1</c>이 같은 코드를 쓴다(<see cref="MeaningGenerator"/>와 같은 이유).
/// 예전에는 관리자 엔드포인트 안의 지역 함수여서, 호출자가 둘이 되면 "못 찾은 것도 기억한다"
/// 같은 규칙이 한쪽에만 남을 위험이 있었다.
///
/// 전부 조용히 강등된다 — 커버가 없거나 Last.fm이 끊겨도 가사는 멀쩡해야 한다.
/// </summary>
public sealed class SongExtrasService(LyricsStore store, CoverArt covers, LastFmAccount lastfm)
{
    /// <summary>연결된 Last.fm 계정이 있는가(없으면 좋아요 기능 전체가 꺼진 것이다).</summary>
    public bool LoveConnected =>
        !string.IsNullOrEmpty(store.GetSetting(LastFmAccount.SessionSetting))
        && !string.IsNullOrEmpty(store.GetSetting(LastFmAccount.UserSetting));

    /// <summary>
    /// 커버를 포함한 곡 링크. <b>아직 안 찾아본 곡이면 지금 찾는다</b> — 이게 없으면
    /// 관리자 화면을 열어 본 곡에만 커버가 생겨 앱에서는 대부분 비어 보인다.
    /// 못 찾은 것도 저장하므로 다음부터는 외부 API를 부르지 않는다.
    /// </summary>
    public async Task<SongLinks> ResolveAsync(LyricsEntry entry, CancellationToken ct = default)
    {
        var links = store.GetSongLinks(entry.Key ?? "");
        if (links.CoverTried) return links;

        var found = await RefindCoverAsync(entry, ct).ConfigureAwait(false);
        return links with { CoverUrl = found?.Url, CoverSource = found?.Source, CoverAt = "now" };
    }

    /// <summary>커버를 처음부터 다시 찾는다. <b>못 찾아도 저장한다</b>(음성 캐시).</summary>
    public async Task<CoverImage?> RefindCoverAsync(LyricsEntry entry, CancellationToken ct = default)
    {
        var found = await covers.FindAsync(entry.Title, entry.Artist, ct).ConfigureAwait(false);
        store.SetCover(entry.Key ?? "", found?.Url, found?.Source);
        return found;
    }

    /// <summary>
    /// 좋아요 여부. <b>모르면 <see cref="LoveState.Known"/>가 false다</b> —
    /// 모르는 것을 "좋아요 안 함"으로 그리면 이미 켜 둔 곡을 끄게 된다.
    /// </summary>
    public async Task<LoveState> LoveAsync(LyricsEntry entry, CancellationToken ct = default)
    {
        var user = store.GetSetting(LastFmAccount.UserSetting);
        if (!LoveConnected || user is null) return LoveState.NotConnected;

        var state = await lastfm.GetStateAsync(entry.Title, entry.Artist, user, ct).ConfigureAwait(false);
        if (state is null) return new LoveState(true, false, false);

        // 정식 곡 주소는 알아낸 김에 기억해 둔다 — 규칙으로 만든 주소를 쓰지 않게.
        if (state.Url is not null) store.SetLastFmUrl(entry.Key ?? "", state.Url);
        return new LoveState(true, true, state.Loved);
    }

    /// <summary>좋아요를 켜거나 끈다. 계정이 없거나 실패하면 false.</summary>
    public async Task<bool> SetLovedAsync(LyricsEntry entry, bool loved, CancellationToken ct = default)
    {
        var session = store.GetSetting(LastFmAccount.SessionSetting);
        return !string.IsNullOrEmpty(session)
            && await lastfm.SetLovedAsync(entry.Title, entry.Artist, loved, session, ct).ConfigureAwait(false);
    }
}
