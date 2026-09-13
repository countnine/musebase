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
public sealed class SongExtrasService(
    LyricsStore store, CoverArt covers, LastFmAccount lastfm, SpotifyAccount spotify)
{
    /// <summary>연결된 Spotify 계정이 있는가.</summary>
    public bool SpotifyConnected =>
        spotify.CanConnect && !string.IsNullOrEmpty(store.GetSetting(SpotifyAccount.RefreshSetting));

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

    /// <summary>
    /// Spotify 라이브러리에 담겨 있는가. 트랙 URI는 <b>한 번만 찾고 기억한다</b>(못 찾은 것도) —
    /// 곡을 열 때마다 검색을 되풀이하면 한도가 금세 찬다.
    /// </summary>
    public async Task<SpotifyState> SpotifyAsync(LyricsEntry entry, CancellationToken ct = default)
    {
        var refresh = store.GetSetting(SpotifyAccount.RefreshSetting);
        if (!spotify.CanConnect || string.IsNullOrEmpty(refresh)) return SpotifyState.NotConnected;

        var key = entry.Key ?? "";
        var links = store.GetSongLinks(key);

        // 찾아봤는데 없던 곡이면 다시 찾지 않는다(Spotify에 아예 없는 곡도 많다).
        if (links.SpotifyTried && links.SpotifyUri is null) return new SpotifyState(true, true, false);

        var state = await spotify
            .GetStateAsync(entry.Title, entry.Artist, refresh!, links.SpotifyUri, ct).ConfigureAwait(false);

        // 아직 안 찾아본 곡의 결과만 기억한다 — 실패(null)를 "없음"으로 굳히면 안 된다.
        if (!links.SpotifyTried && state is not null) store.SetSpotifyUri(key, state.Uri);

        return state is null ? new SpotifyState(true, false, false) : new SpotifyState(true, true, state.Saved);
    }

    /// <summary>
    /// 좋아요를 켜거나 끈다 — <b>Last.fm과 Spotify 양쪽에</b>.
    ///
    /// 한쪽이 실패해도 다른 쪽은 그대로 둔다. 대신 <b>어느 쪽이 안 됐는지 돌려준다</b> —
    /// 절반만 반영된 것을 성공으로 보고하면 사람이 저쪽도 됐다고 믿는다.
    /// Spotify를 연결하지 않았으면 <see cref="LoveResult.Spotify"/>는 <c>null</c>(해당 없음)이다.
    /// </summary>
    public async Task<LoveResult> SetLovedAsync(LyricsEntry entry, bool loved, CancellationToken ct = default)
    {
        var session = store.GetSetting(LastFmAccount.SessionSetting);
        var lastFmOk = !string.IsNullOrEmpty(session)
            && await lastfm.SetLovedAsync(entry.Title, entry.Artist, loved, session, ct).ConfigureAwait(false);

        return new LoveResult(lastFmOk, await SpotifyLoveAsync(entry, loved, ct).ConfigureAwait(false));
    }

    /// <summary>Spotify 쪽만. 연결돼 있지 않으면 null(해당 없음), 시도했으면 성공 여부.</summary>
    private async Task<bool?> SpotifyLoveAsync(LyricsEntry entry, bool loved, CancellationToken ct)
    {
        var refresh = store.GetSetting(SpotifyAccount.RefreshSetting);
        if (!spotify.CanConnect || string.IsNullOrEmpty(refresh)) return null;

        var key = entry.Key ?? "";
        var links = store.GetSongLinks(key);
        var uri = links.SpotifyUri;

        if (uri is null)
        {
            if (links.SpotifyTried) return false;    // 찾아봤는데 없는 곡 — 담을 대상이 없다
            var state = await spotify
                .GetStateAsync(entry.Title, entry.Artist, refresh!, null, ct).ConfigureAwait(false);
            if (state is null) return false;
            store.SetSpotifyUri(key, state.Uri);
            uri = state.Uri;
        }

        return await spotify.SetSavedAsync(uri, loved, refresh!, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// 좋아요 반영 결과. <paramref name="Spotify"/>가 <c>null</c>이면 연결돼 있지 않아 시도하지 않은 것이다
/// — 실패(<c>false</c>)와 구별해야 사람에게 할 말이 달라진다.
/// </summary>
public sealed record LoveResult(bool LastFm, bool? Spotify)
{
    /// <summary>시도한 곳이 전부 됐는가.</summary>
    public bool AllOk => LastFm && Spotify is not false;
}
