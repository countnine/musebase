using System.Security.Cryptography;
using System.Text;
using Musebase.Core;

namespace Musebase.Server;

/// <summary>
/// 관리자 쿠키 서명·검증. **원 토큰을 쿠키에 굽지 않는다** — 만료 시각 + HMAC만 담는다.
/// 브라우저는 `Authorization` 헤더를 붙일 수 없어서 API와 다른 방식이 필요했다.
/// </summary>
public static class AdminAuth
{
    /// <summary>쿠키 값 = "{만료 유닉스초}.{HMAC-SHA256(비밀, 만료)}" (base64url).</summary>
    public static string Sign(string secret, long expiresUnix)
    {
        var mac = Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expiresUnix.ToString())))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{expiresUnix}.{mac}";
    }

    /// <summary>쿠키 검증 — 형식·만료·서명(고정시간 비교)을 모두 본다.</summary>
    public static bool Verify(string? cookie, string secret, long nowUnix)
    {
        if (string.IsNullOrEmpty(cookie)) return false;
        var dot = cookie!.IndexOf('.');
        if (dot <= 0) return false;
        if (!long.TryParse(cookie[..dot], out var expires) || expires <= nowUnix) return false;

        var expected = Encoding.UTF8.GetBytes(Sign(secret, expires));
        var provided = Encoding.UTF8.GetBytes(cookie);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>편집·삭제 폼의 CSRF 토큰(세션 쿠키에서 파생 — 쿠키를 모르면 만들 수 없다).</summary>
    public static string Csrf(string secret, string cookieValue) =>
        Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("csrf:" + cookieValue)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static bool VerifyCsrf(string? provided, string secret, string cookieValue)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Csrf(secret, cookieValue)), Encoding.UTF8.GetBytes(provided!));
    }
}

/// <summary>검색어 → SQL LIKE 패턴.</summary>
public static class AdminQuery
{
    /// <summary>
    /// 사용자 검색어를 `%…%` 패턴으로 만든다. `%`·`_`·`\`는 리터럴로 취급해야 하므로
    /// 역슬래시로 이스케이프하고 SQL 쪽에서 <c>ESCAPE '\'</c>를 쓴다. 비어 있으면 null(=전체 목록).
    /// </summary>
    public static string? ToLikePattern(string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return null;

        var sb = new StringBuilder(q!.Length + 8).Append('%');
        foreach (var c in q.ToLowerInvariant())
        {
            if (c is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('%').ToString();
    }
}

/// <summary>표시용 시각 계산. 저장은 UTC, 화면은 로컬(기본 KST).</summary>
public static class AdminTime
{
    /// <summary>로컬 기준 "오늘 자정"을 UTC 문자열로. 이걸 빼먹으면 밤 시간대 조회가 내일로 집계된다.</summary>
    public static string TodayStartUtc(DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var midnight = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return midnight.ToUniversalTime().ToString(LyricsStore.TimeFormat);
    }

    public static string DaysAgoUtc(DateTimeOffset nowUtc, int days) =>
        nowUtc.AddDays(-days).ToString(LyricsStore.TimeFormat);

    /// <summary>ISO-8601 UTC 문자열 → 로컬 표시 문자열. 파싱 실패 시 원문 그대로.</summary>
    public static string ToLocal(string? utcIso, TimeZoneInfo tz, string format = "MM-dd HH:mm")
    {
        if (string.IsNullOrEmpty(utcIso)) return "";
        if (!DateTimeOffset.TryParse(utcIso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return utcIso!;
        return TimeZoneInfo.ConvertTime(parsed.ToUniversalTime(), tz).ToString(format);
    }

    /// <summary>"3분 전" 같은 상대 표기(마지막 조회가 언제였는지가 가장 중요한 정보다).</summary>
    public static string Ago(string? utcIso, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrEmpty(utcIso)) return "없음";
        if (!DateTimeOffset.TryParse(utcIso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return utcIso!;
        var delta = nowUtc - parsed.ToUniversalTime();
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        return delta.TotalSeconds switch
        {
            < 60 => "방금",
            < 3600 => $"{(int)delta.TotalMinutes}분 전",
            < 86400 => $"{(int)delta.TotalHours}시간 전",
            _ => $"{(int)delta.TotalDays}일 전",
        };
    }
}

/// <summary>요청에서 기기 라벨을 뽑는다(순수 함수 — 테스트 가능).</summary>
public static class DeviceLabel
{
    /// <summary>
    /// 우선순위: 전용 헤더 → 매핑된 IP → Musebase 앱의 User-Agent → IP → "unknown".
    ///
    /// 서버는 루프백만 리슨하고 tailscale serve가 프록시하므로 소스 IP는 127.0.0.1이고
    /// 실제 테일넷 주소는 X-Forwarded-For에 온다(실측 확인). 브라우저·curl의 UA는 기기 식별에
    /// 쓸모가 없으므로 <b>Musebase가 보낸 UA만</b> 인정하고, 그 외에는 IP를 쓴다.
    /// </summary>
    public static string Resolve(
        string? deviceHeader, string? userAgent, string? forwardedFor, string? remoteIp,
        IReadOnlyDictionary<string, string> labels)
    {
        if (!string.IsNullOrWhiteSpace(deviceHeader)) return Map(deviceHeader!.Trim(), labels);

        var ip = FirstForwarded(forwardedFor) ?? remoteIp?.Trim();
        if (!string.IsNullOrWhiteSpace(ip) && labels.TryGetValue(ip!, out var named)) return named;

        var ua = userAgent?.Trim();
        if (ua is { Length: > 0 } && ua.StartsWith("Musebase", StringComparison.OrdinalIgnoreCase)) return ua;

        return string.IsNullOrWhiteSpace(ip) ? "unknown" : ip!;
    }

    private static string? FirstForwarded(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var first = header!.Split(',')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    private static string Map(string value, IReadOnlyDictionary<string, string> labels) =>
        labels.TryGetValue(value, out var named) ? named : value;

    /// <summary>"100.1.2.3=거실PC,100.4.5.6=갤럭시" → 사전. 형식이 틀린 항목은 조용히 건너뛴다.</summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(string? spec)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(spec)) return map;
        foreach (var pair in spec!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1) continue;
            map[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return map;
    }
}

/// <summary>LRC를 상세 화면용 줄 목록으로 분해한다(원문과 번역을 나란히 보여주기 위해).</summary>
public static class AdminLrc
{
    /// <summary>
    /// 확장 LRC → 표시용 줄. <paramref name="lang"/>이 있으면 그 언어 번역을, 없으면 아무 번역이나 붙인다.
    /// 파싱이 안 되면 빈 목록(호출부가 원문을 그대로 보여준다 — 무손실 보관 원칙).
    /// </summary>
    public static IReadOnlyList<DisplayLine> ToDisplayLines(string lrc, string? lang)
    {
        var parsed = Lyrics.Parse(lrc);
        if (parsed is null) return Array.Empty<DisplayLine>();

        var rows = new List<DisplayLine>(parsed.Lines.Count);
        foreach (var line in parsed.Lines)
        {
            var translation = string.IsNullOrWhiteSpace(lang)
                ? line.Attachments.Translation()
                : line.Attachments.Translation(lang!.ToLowerInvariant());
            rows.Add(new DisplayLine(
                TimeSpan.FromSeconds(line.Position).ToString(@"mm\:ss\.ff"),
                line.Content,
                string.IsNullOrWhiteSpace(translation) ? null : translation));
        }
        return rows;
    }

    /// <summary>LRC에 실린 번역 언어 태그 목록(상세 화면의 언어 선택용). 언어 미상 제공자 번역은 제외.</summary>
    public static IReadOnlyList<string> TranslationTags(string lrc)
    {
        var facts = LyricsFacts.From(lrc);
        return facts.Langs.Where(l => l != "*").ToArray();
    }
}
