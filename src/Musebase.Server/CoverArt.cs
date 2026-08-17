using System.Text.Json;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>찾아낸 커버 한 장.</summary>
/// <param name="Source">어디서 왔는지 — CSP 허용 목록과 짝이 맞아야 한다(AdminEndpoints).</param>
public sealed record CoverImage(string Url, string Source);

/// <summary>
/// 곡의 커버 이미지를 찾는다. 키가 필요 없는 두 곳을 순서대로 본다.
///
/// <b>Last.fm은 쓰지 않는다.</b> API 응답에 앨범 이미지가 들어 있지만, Last.fm API 약관은
/// "audio, audiovisual materials, and artwork ... expressly excluded from this Agreement"라고
/// 명시한다 — 가져올 수 있다는 것과 써도 된다는 것은 다르다.
///
/// MusicBrainz + Cover Art Archive는 요청이 2단(recording→release-group)이고 초당 1회 제한에
/// 커버 누락이 잦아(실측 404) 쓰지 않는다.
/// </summary>
public sealed class CoverArt
{
    public const string ITunes = "itunes";
    public const string Deezer = "deezer";

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public CoverArt(HttpClient? http = null, int timeoutMs = 2500)
    {
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    /// <summary>못 찾으면 null. 호출자는 그 사실도 저장해 다음에 다시 부르지 않는다.</summary>
    public async Task<CoverImage?> FindAsync(string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return await FromITunesAsync(title, artist ?? "", ct).ConfigureAwait(false)
            ?? await FromDeezerAsync(title, artist ?? "", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// iTunes Search — 키가 필요 없고 곡 단위 커버리지가 가장 좋다.
    /// <b>첫 결과를 그냥 믿지 않는다</b>: 검색 API는 무엇을 넣든 뭔가를 돌려주므로
    /// Genius·Musixmatch와 같은 기준(<see cref="MeaningMatch.IsSameSong"/>)을 통과시킨다.
    /// </summary>
    private async Task<CoverImage?> FromITunesAsync(string title, string artist, CancellationToken ct)
    {
        var term = Uri.EscapeDataString(MeaningLinks.Query(title, artist));
        var json = await GetJsonAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=5", ct)
            .ConfigureAwait(false);
        if (json is null || !json.Value.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array) return null;

        foreach (var hit in results.EnumerateArray())
        {
            if (!MeaningMatch.IsSameSong(Text(hit, "trackName"), Text(hit, "artistName"), title, artist)) continue;
            var art = Text(hit, "artworkUrl100");
            if (art is null) continue;
            return new CoverImage(Promote(art), ITunes);
        }
        return null;
    }

    /// <summary>Deezer — 역시 키가 없어도 되고, iTunes에 없는 곡을 가끔 메워 준다.</summary>
    private async Task<CoverImage?> FromDeezerAsync(string title, string artist, CancellationToken ct)
    {
        var query = artist.Length == 0
            ? $"track:\"{title}\""
            : $"artist:\"{artist}\" track:\"{title}\"";
        var json = await GetJsonAsync(
            $"https://api.deezer.com/search?q={Uri.EscapeDataString(query)}&limit=5", ct).ConfigureAwait(false);
        if (json is null || !json.Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array) return null;

        foreach (var hit in data.EnumerateArray())
        {
            var hitArtist = hit.TryGetProperty("artist", out var a) ? Text(a, "name") : null;
            if (!MeaningMatch.IsSameSong(Text(hit, "title"), hitArtist, title, artist)) continue;
            if (!hit.TryGetProperty("album", out var album)) continue;
            var cover = Text(album, "cover_big") ?? Text(album, "cover_medium");
            if (cover is null) continue;
            return new CoverImage(cover, Deezer);
        }
        return null;
    }

    /// <summary>
    /// iTunes 아트워크 주소는 끝의 크기가 그대로 파일명이다 — 100을 600으로 바꾸면 큰 그림이 온다.
    /// 형태가 예상과 다르면 건드리지 않는다(억지로 만들면 404가 된다).
    /// </summary>
    public static string Promote(string artworkUrl) =>
        artworkUrl.EndsWith("/100x100bb.jpg", StringComparison.Ordinal)
            ? artworkUrl[..^"100x100bb.jpg".Length] + "600x600bb.jpg"
            : artworkUrl;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var body = await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null; // 조용한 강등 — 커버가 없다고 곡 상세가 안 뜨면 안 된다
        }
    }
}
