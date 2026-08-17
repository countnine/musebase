using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>이 곡에 대해 Last.fm이 알려 준 것 — 좋아요 여부와 정식 곡 주소.</summary>
public sealed record LastFmTrackState(bool Loved, string? Url);

/// <summary>
/// Last.fm **계정** API — 좋아요를 읽고 켜고 끈다.
///
/// <see cref="LastFmSource"/>(의미 자료 수집)와 같은 서비스지만 성격이 다르다. 저쪽은 API 키만으로
/// 되는 공개 데이터고 이쪽은 <b>그 사람의 계정에 쓰는</b> 인증 호출이다. 관리자 화면 전용이라
/// <c>Musebase.Core</c>가 아니라 서버에 둔다.
///
/// 두 가지를 계속 조심해야 한다.
/// ① <b>오류가 HTTP 200에 담겨 온다</b> — 본문의 <c>error</c> 코드를 반드시 본다.
/// ② 쓰기 호출은 <b>POST + 서명</b>이다. 서명 규칙은 <see cref="Signature"/> 참고.
/// 실패는 예외가 아니라 null/false로 강등한다 — 곡 상세가 이것 때문에 안 뜨면 안 된다.
/// </summary>
public sealed class LastFmAccount
{
    private const string Endpoint = "https://ws.audioscrobbler.com/2.0/";

    /// <summary>승인 페이지. 여기로 사람을 보내면 돌아올 때 <c>?token=</c>이 붙는다.</summary>
    public const string AuthPage = "https://www.last.fm/api/auth/";

    /// <summary>세션 키를 담아 두는 설정 이름(<see cref="LyricsStore.GetSetting"/>).</summary>
    public const string SessionSetting = "lastfm.session";
    public const string UserSetting = "lastfm.user";

    private readonly string _apiKey;
    private readonly string _secret;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public LastFmAccount(string? apiKey, string? secret, HttpClient? http = null, int timeoutMs = 2500)
    {
        _apiKey = (apiKey ?? "").Trim();
        _secret = (secret ?? "").Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    /// <summary>읽기(좋아요 여부)에 쓸 수 있는가 — API 키만 있으면 된다.</summary>
    public bool CanRead => _apiKey.Length > 0;

    /// <summary>계정 연결(승인 플로우)을 할 수 있는가 — shared secret까지 있어야 한다.</summary>
    public bool CanConnect => _apiKey.Length > 0 && _secret.Length > 0;

    /// <summary>사람을 보낼 승인 페이지 주소. 돌아올 곳을 <c>cb</c>로 그때그때 넘긴다.</summary>
    public string AuthorizeUrl(string callback) =>
        $"{AuthPage}?api_key={Uri.EscapeDataString(_apiKey)}&cb={Uri.EscapeDataString(callback)}";

    /// <summary>
    /// 서명 = 파라미터를 <b>이름순</b>으로 <c>&lt;이름&gt;&lt;값&gt;</c>으로 이어붙이고 shared secret을
    /// 뒤에 붙여 MD5. <c>format</c>과 <c>api_sig</c> 자신은 넣지 않는다.
    ///
    /// MD5는 Last.fm 규격이라 쓰는 것이고 <b>보안 용도가 아니다</b>(우리가 고를 수 있는 값이 없다).
    /// </summary>
    public static string Signature(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(name).Append(value);
        sb.Append(secret);

        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// 승인 토큰을 세션 키로 바꾼다. <b>토큰은 1회용</b>이라 실패하면 처음부터 다시 승인해야 한다.
    /// 성공하면 (세션 키, 아이디).
    /// </summary>
    public async Task<(string Session, string User)?> ExchangeTokenAsync(string token, CancellationToken ct = default)
    {
        if (!CanConnect || string.IsNullOrWhiteSpace(token)) return null;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = _apiKey,
            ["method"] = "auth.getSession",
            ["token"] = token.Trim(),
        };

        var json = await GetAsync(parameters, ct).ConfigureAwait(false);
        if (json is null) return null;

        try
        {
            var session = json.Value.GetProperty("session");
            var key = session.GetProperty("key").GetString();
            var name = session.TryGetProperty("name", out var n) ? n.GetString() : null;
            return string.IsNullOrWhiteSpace(key) ? null : (key!, name ?? "");
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 좋아요 여부와 정식 곡 주소. <paramref name="user"/>를 함께 넘겨야 <c>userloved</c>가 실린다.
    /// 모르면 null — 호출자는 <b>"좋아요 안 함"으로 그리지 않는다</b>.
    /// </summary>
    public async Task<LastFmTrackState?> GetStateAsync(
        string title, string artist, string user, CancellationToken ct = default)
    {
        if (!CanRead || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(user)) return null;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = _apiKey,
            ["method"] = "track.getInfo",
            ["artist"] = artist ?? "",
            ["track"] = title,
            ["username"] = user,
            ["autocorrect"] = "1",
        };

        // 읽기 전용이라 서명이 필요 없다(secret이 없어도 동작해야 한다).
        var json = await GetAsync(parameters, ct, sign: false).ConfigureAwait(false);
        if (json is null || !json.Value.TryGetProperty("track", out var track)) return null;

        // userloved는 문자열 "0"/"1"로 온다 — 숫자로 오는 경우도 방어한다.
        var loved = track.TryGetProperty("userloved", out var lv) && lv.ValueKind switch
        {
            JsonValueKind.String => lv.GetString() == "1",
            JsonValueKind.Number => lv.GetInt32() == 1,
            JsonValueKind.True => true,
            _ => false,
        };
        var url = track.TryGetProperty("url", out var u) ? u.GetString() : null;
        return new LastFmTrackState(loved, string.IsNullOrWhiteSpace(url) ? null : url);
    }

    /// <summary>좋아요를 켜거나 끈다. 세션 키가 없으면 아무 일도 하지 않는다.</summary>
    public async Task<bool> SetLovedAsync(
        string title, string artist, bool loved, string sessionKey, CancellationToken ct = default)
    {
        if (!CanConnect || string.IsNullOrWhiteSpace(sessionKey) || string.IsNullOrWhiteSpace(title))
            return false;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = _apiKey,
            ["method"] = loved ? "track.love" : "track.unlove",
            ["artist"] = artist ?? "",
            ["track"] = title,
            ["sk"] = sessionKey,
        };
        parameters["api_sig"] = Signature(parameters, _secret);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            // 쓰기는 반드시 POST이고 method까지 본문에 담는다(쿼리스트링에 두면 실패한다).
            var fields = parameters.ToDictionary(p => p.Key, p => p.Value);
            fields["format"] = "json";
            using var response = await _http
                .PostAsync(Endpoint, new FormUrlEncodedContent(fields), cts.Token).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode && ErrorCode(body) is null;
        }
        catch (Exception)
        {
            return false; // 조용한 강등
        }
    }

    /// <summary>GET 한 번. 오류(본문 <c>error</c> 포함)면 null.</summary>
    private async Task<JsonElement?> GetAsync(
        Dictionary<string, string> parameters, CancellationToken ct, bool sign = true)
    {
        try
        {
            if (sign) parameters["api_sig"] = Signature(parameters, _secret);

            var query = string.Join("&", parameters
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var body = await _http.GetStringAsync($"{Endpoint}?{query}&format=json", cts.Token).ConfigureAwait(false);
            if (ErrorCode(body) is not null) return null;

            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null; // 조용한 강등
        }
    }

    /// <summary>본문에 실려 온 오류 코드(정상이면 null). <b>HTTP 200에도 실려 온다.</b></summary>
    private static int? ErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var e) && e.TryGetInt32(out var code)
                ? code : null;
        }
        catch (JsonException)
        {
            return -1; // JSON이 아니면 정상일 리 없다
        }
    }
}
