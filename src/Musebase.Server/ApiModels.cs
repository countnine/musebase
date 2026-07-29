using System.Text.Json.Serialization;

namespace Musebase.Server;

/// <summary>
/// 가사 1건. 계약은 <c>contracts/lyrics-api.md</c>(v1) — JSON은 camelCase다
/// (PlaybackViewState의 PascalCase와 다르므로 주의).
/// 요청(PUT)은 <see cref="Title"/>/<see cref="Artist"/>/<see cref="Lrc"/>/<see cref="Service"/>/
/// <see cref="Origin"/>만 채우면 되고, 나머지는 서버가 계산해 응답에 싣는다.
/// </summary>
public sealed record LyricsEntry
{
    public string? Key { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    /// <summary>확장 LRC 전문(번역 첨부 포함). 서버는 이 문자열을 변형하지 않고 그대로 보관한다.</summary>
    public required string Lrc { get; init; }
    public string? Service { get; init; }
    /// <summary>"provider" 또는 "user"(사용자 편집본 — 자동 검색이 덮어쓰지 못한다).</summary>
    public string Origin { get; init; } = OriginProvider;

    public string[]? Langs { get; init; }
    public int? LineCount { get; init; }
    public bool? HasInlineTimeTags { get; init; }
    public int? Revision { get; init; }
    public string? UpdatedAt { get; init; }
    /// <summary>"exact" 또는 "cleaned" — 어떤 키로 맞았는지(진단용).</summary>
    public string? Match { get; init; }

    public const string OriginProvider = "provider";
    public const string OriginUser = "user";
    public const string MatchExact = "exact";
    public const string MatchCleaned = "cleaned";
}

/// <summary>PUT이 병합 정책으로 거부됐을 때의 응답(202).</summary>
public sealed record PutRejected(bool Accepted, string Reason)
{
    public static PutRejected UserEditProtected { get; } = new(false, "user-edit-protected");
    public static PutRejected PoorerContent { get; } = new(false, "poorer-content");
}

/// <summary>GET /v1/stats — 검증·디버깅용 요약.</summary>
public sealed record ServerStats(int Songs, int WithTranslation, string? LastUpdatedAt);

/// <summary>JSON 오류 본문.</summary>
public sealed record ApiError([property: JsonPropertyName("error")] string Error);
