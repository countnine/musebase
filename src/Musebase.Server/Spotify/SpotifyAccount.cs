using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>이 곡의 Spotify 상태 — 라이브러리에 담겨 있는가와 그 트랙의 URI.</summary>
public sealed record SpotifyTrackState(bool Saved, string Uri);

/// <summary>
/// Spotify 라이브러리("좋아요한 곡")를 읽고 쓴다 — Last.fm 좋아요를 Spotify에도 함께 반영하려는 것.
///
/// <b>2026년 2월에 API가 크게 바뀌었다.</b> 두 가지를 계속 조심해야 한다.
/// ① <c>PUT /me/tracks</c>는 <b>폐기됐다</b> — 지금은 <c>PUT /me/library?uris=spotify:track:…</c>로
///    통합됐고(한 번에 40개까지), 지우는 것은 <c>DELETE /me/library</c>다. 옛 엔드포인트는 토큰과
///    스코프가 맞아도 403을 준다.
/// ② Development Mode는 <b>앱 소유자에게 Spotify Premium 구독을 요구</b>하고 인가 사용자를
///    5명으로 제한한다. Extended quota는 법인·250K MAU 요건이라 개인에게는 열리지 않는다 —
///    개인용으로는 Development Mode가 정답이고, <b>Premium이 끊기면 이 기능도 함께 멈춘다</b>.
///
/// 또 하나 Last.fm과 다른 점: <b>콜백 주소를 앱 대시보드에 미리 등록해야 한다</b>(Last.fm은
/// 요청할 때 넘길 수 있었다). 그래서 관리 화면이 등록할 주소를 그대로 띄워 준다.
///
/// SMTC·가사에는 트랙 ID가 없으므로 제목·아티스트로 검색해 찾고, 커버 아트와 같은 판정
/// (<see cref="MeaningMatch.IsSameSong"/>)으로 <b>정말 그 곡인지 확인</b>한 뒤에만 쓴다.
/// 실패는 전부 null/false로 조용히 강등한다 — 곡 상세가 이것 때문에 안 뜨면 안 된다.
/// </summary>
public sealed class SpotifyAccount
{
    private const string AuthPage = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private const string ApiBase = "https://api.spotify.com/v1";

    /// <summary>라이브러리를 읽고 쓰는 데 필요한 최소 스코프.</summary>
    public const string Scopes = "user-library-read user-library-modify";

    /// <summary>갱신 토큰과 아이디를 담아 두는 설정 이름(<see cref="LyricsStore.GetSetting"/>).</summary>
    public const string RefreshSetting = "spotify.refresh";
    public const string UserSetting = "spotify.user";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    // 액세스 토큰은 짧게 살고(보통 1시간) 다시 받으면 그만이라 메모리에만 둔다.
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    public SpotifyAccount(string? clientId, string? clientSecret, HttpClient? http = null, int timeoutMs = 4000)
    {
        _clientId = (clientId ?? "").Trim();
        _clientSecret = (clientSecret ?? "").Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    /// <summary>계정을 연결할 수 있는 구성인가(둘 다 있어야 한다).</summary>
    public bool CanConnect => _clientId.Length > 0 && _clientSecret.Length > 0;

    /// <summary>사람을 보낼 승인 페이지. <paramref name="callback"/>은 앱 대시보드에 등록된 값이어야 한다.</summary>
    public string AuthorizeUrl(string callback, string state) =>
        $"{AuthPage}?client_id={Uri.EscapeDataString(_clientId)}"
        + "&response_type=code"
        + $"&redirect_uri={Uri.EscapeDataString(callback)}"
        + $"&scope={Uri.EscapeDataString(Scopes)}"
        + $"&state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// 승인 코드를 갱신 토큰과 아이디로 바꾼다. 실패하면 null.
    /// 갱신 토큰은 <b>이 한 번만 받을 수 있다</b> — 저장에 실패하면 사람이 다시 승인해야 한다.
    /// </summary>
    public async Task<(string Refresh, string User)?> ExchangeCodeAsync(
        string code, string callback, CancellationToken ct = default)
    {
        if (!CanConnect || string.IsNullOrWhiteSpace(code)) return null;

        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = callback,
        }, ct).ConfigureAwait(false);

        if (token is not { Refresh: { Length: > 0 } refresh, Access: { Length: > 0 } access }) return null;

        var user = await MeAsync(access, ct).ConfigureAwait(false);
        return user is null ? null : (refresh, user);
    }

    /// <summary>
    /// 이 곡이 라이브러리에 있는가. 없는 곡·못 찾은 곡·실패는 전부 null —
    /// <b>"저장 안 됨"과 "모름"을 섞지 않는다</b>(Last.fm 좋아요와 같은 이유다).
    /// </summary>
    public async Task<SpotifyTrackState?> GetStateAsync(
        string title, string artist, string refresh, string? knownUri, CancellationToken ct = default)
    {
        var access = await AccessAsync(refresh, ct).ConfigureAwait(false);
        if (access is null) return null;

        var uri = knownUri ?? await FindUriAsync(title, artist, access, ct).ConfigureAwait(false);
        if (uri is null) return null;

        var body = await SendAsync(
            HttpMethod.Get, $"{ApiBase}/me/library/contains?uris={Uri.EscapeDataString(uri)}", access, ct)
            .ConfigureAwait(false);
        if (body is null) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var first = document.RootElement.EnumerateArray().FirstOrDefault();
            return new SpotifyTrackState(first.ValueKind == JsonValueKind.True, uri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>라이브러리에 담거나 뺀다. 성공하면 true.</summary>
    public async Task<bool> SetSavedAsync(
        string uri, bool saved, string refresh, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;

        var access = await AccessAsync(refresh, ct).ConfigureAwait(false);
        if (access is null) return false;

        // 2026-02부터 저장·삭제가 /me/library 하나로 통합됐다(옛 /me/tracks는 403).
        var method = saved ? HttpMethod.Put : HttpMethod.Delete;
        var url = $"{ApiBase}/me/library?uris={Uri.EscapeDataString(uri)}";
        return await SendAsync(method, url, access, ct).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// 제목·아티스트로 트랙 URI를 찾는다. 첫 결과를 믿지 않고 <b>같은 곡인지 확인</b>한다 —
    /// 검색은 리믹스·커버·노래방 음원을 곧잘 위로 올린다.
    /// </summary>
    public async Task<string?> FindUriAsync(
        string title, string artist, string access, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        // 2026-02부터 limit 상한이 50 → 10으로 내려갔다.
        var url = $"{ApiBase}/search?type=track&limit=10&q={Uri.EscapeDataString(query)}";

        var body = await SendAsync(HttpMethod.Get, url, access, ct).ConfigureAwait(false);
        if (body is null) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("tracks", out var tracks)
                || !tracks.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var by = item.TryGetProperty("artists", out var artists)
                    && artists.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", artists.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null)
                        .Where(a => !string.IsNullOrWhiteSpace(a)))
                    : "";
                var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : null;

                // 인자 순서는 (검색 결과, 찾던 곡) — 커버 아트가 쓰는 것과 같다.
                if (uri is { Length: > 0 } && MeaningMatch.IsSameSong(name, by, title, artist))
                    return uri;
            }
        }
        catch (JsonException)
        {
            // 아래에서 null
        }

        return null;
    }

    // ---- 토큰 ----

    /// <summary>쓸 수 있는 액세스 토큰(없거나 곧 만료면 갱신 토큰으로 다시 받는다).</summary>
    private async Task<string?> AccessAsync(string refresh, CancellationToken ct)
    {
        if (!CanConnect || string.IsNullOrWhiteSpace(refresh)) return null;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 만료 직전에 받은 토큰으로 요청을 보내면 401이 난다 — 1분 여유를 둔다.
            if (_accessToken is { Length: > 0 } && DateTimeOffset.UtcNow < _accessExpires.AddMinutes(-1))
                return _accessToken;

            var token = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
            }, ct).ConfigureAwait(false);

            if (token is not { Access: { Length: > 0 } access }) return null;

            _accessToken = access;
            _accessExpires = DateTimeOffset.UtcNow.AddSeconds(token.Value.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<(string? Access, string? Refresh, int ExpiresIn)?> PostTokenAsync(
        Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(fields),
            };
            // 서버가 비밀을 보관할 수 있으므로 confidential 방식(Basic)을 쓴다 — PKCE보다 단순하다.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return (
                root.TryGetProperty("access_token", out var a) ? a.GetString() : null,
                // 갱신 요청에는 refresh_token이 안 올 수도 있다(그러면 쓰던 것을 계속 쓴다).
                root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
                root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600);
        }
        catch (Exception)
        {
            return null; // 조용한 강등
        }
    }

    private async Task<string?> MeAsync(string access, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, $"{ApiBase}/me", access, ct).ConfigureAwait(false);
        if (body is null) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var name = root.TryGetProperty("display_name", out var d) ? d.GetString() : null;
            var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            return string.IsNullOrWhiteSpace(name) ? id : name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>요청 한 번. 성공이면 본문(없으면 빈 문자열), 실패면 null.</summary>
    private async Task<string?> SendAsync(HttpMethod method, string url, string access, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // 조용한 강등
        }
    }
}
