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
    /// <summary>서버에 없었다. 조회 기록에만 쓰이는 값이다(항목 자체에는 실리지 않는다).</summary>
    public const string MatchMiss = "miss";
}

/// <summary>PUT이 병합 정책으로 거부됐을 때의 응답(202).</summary>
public sealed record PutRejected(bool Accepted, string Reason)
{
    public static PutRejected UserEditProtected { get; } = new(false, "user-edit-protected");
    public static PutRejected PoorerContent { get; } = new(false, "poorer-content");
}

/// <summary>GET /v1/stats — 검증·디버깅용 요약.</summary>
public sealed record ServerStats(int Songs, int WithTranslation, string? LastUpdatedAt);

/// <summary>
/// 곡의 의미 1건. 앱은 <see cref="Summary"/>와 <see cref="Attribution"/>만 보면 되고,
/// <see cref="Sources"/>(원문 JSON)는 관리자 화면·재생성 판단용이다.
///
/// <b>출처 표기는 선택이 아니다</b> — Wikipedia 본문은 CC BY-SA고 Genius·Last.fm도 링크 표기를
/// 요구하므로, 요약을 보여 주는 화면은 <see cref="Attribution"/>을 함께 렌더해야 한다.
/// </summary>
public sealed record MeaningEntry
{
    public string Key { get; init; } = "";
    public required string Title { get; init; }
    public required string Artist { get; init; }
    /// <summary>생성된 대상 언어 문단. `status`가 `ok`가 아니면 null.</summary>
    public string? Summary { get; init; }
    public string Lang { get; init; } = "ko";
    /// <summary>근거로 쓴 원문들(JSON 배열 `[{name,url,text}]`).</summary>
    public string Sources { get; init; } = "[]";
    public string? GeniusUrl { get; init; }
    /// <summary>공식 API로 확인한 Musixmatch 곡 페이지. 규칙으로 만든 주소는 다른 곡으로 갈 수 있어 쓰지 않는다.</summary>
    public string? MusixmatchUrl { get; init; }
    public string? Engine { get; init; }
    public string? Model { get; init; }
    /// <summary>`ok` | `no-source` | `failed`.</summary>
    public string Status { get; init; } = StatusFailed;
    public string UpdatedAt { get; init; } = "";

    /// <summary>화면에 그대로 붙이는 출처 문구(이름·링크 쌍). 응답에 계산해 싣는다.</summary>
    public IReadOnlyList<MeaningAttribution>? Attribution { get; init; }

    public const string StatusOk = "ok";
    public const string StatusNoSource = "no-source";
    public const string StatusFailed = "failed";
}

/// <summary>출처 한 건 — 이름과 원문 주소.</summary>
public sealed record MeaningAttribution(string Name, string? Url);

/// <summary>JSON 오류 본문.</summary>
public sealed record ApiError([property: JsonPropertyName("error")] string Error);

/// <summary>
/// 조회 미스 응답(404). <see cref="Pending"/>가 true면 최근에 **다른 기기**도 같은 곡을 물었다는 뜻이다 —
/// 받는 쪽은 제공자 검색은 그대로 하되 번역을 잠시 미루고 서버를 다시 조회하면 중복 번역을 피한다
/// (`contracts/lyrics-api.md`의 "번역 양보"). 모르는 필드는 무시하면 되므로 하위 호환이다.
/// </summary>
public sealed record NotFoundBody(
    [property: JsonPropertyName("error")] string Error,
    bool Pending,
    int RetryAfterMs);
