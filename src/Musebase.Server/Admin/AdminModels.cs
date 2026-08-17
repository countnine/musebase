namespace Musebase.Server;

/// <summary>조회 기록 1건(관리자 화면 표시용).</summary>
public sealed record LookupRow(string At, string Title, string Artist, string Result, string? Key, string Device);

/// <summary>미스 상위 1건 — 서버에 없어서 각 기기가 직접 검색해야 했던 곡.</summary>
/// <param name="Key">그 뒤에 곡이 올라왔으면 그 키(없으면 null) — 화면에서 가사로 넘어가기 위한 것.</param>
public sealed record MissRow(
    string Title, string Artist, int Count, string LastAt, int Devices, string? Key = null);

/// <summary>광고로 표시해 차단한 제목 1건.</summary>
public sealed record AdTitleRow(string TitleKey, string Title, string Artist, string AddedAt);

/// <summary>기기별 활동.</summary>
public sealed record DeviceRow(string Device, int Lookups, int Hits, string LastAt);

/// <summary>일별 히트/미스(막대 그래프용).</summary>
public sealed record DailyRow(string Day, int Hits, int Misses);

/// <summary>곡 목록 1행(LRC 본문 제외 — 목록은 가볍게).</summary>
/// <param name="MeaningStatus">`ok` | `no-source` | `failed`, 아직 해 본 적 없으면 null.</param>
public sealed record SongRow(
    string Key, string LooseKey, string Title, string Artist, string? Service, string Origin,
    string[] Langs, int LineCount, bool HasInlineTimeTags, int Revision, string UpdatedAt, string? UpdatedBy,
    string? MeaningStatus = null);

/// <summary>기간 내 조회 결과 집계.</summary>
public sealed record HitRate(int Exact, int Cleaned, int Miss)
{
    public int Hits => Exact + Cleaned;
    public int Total => Hits + Miss;

    /// <summary>히트율(%). 조회가 0건이면 0 — 0으로 나누지 않는다.</summary>
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Hits * 100.0 / Total);
}

/// <summary>상세 화면의 가사 한 줄(원문과 번역을 나란히 보여주기 위한 형태).</summary>
public sealed record DisplayLine(string TimeTag, string Content, string? Translation);

/// <summary>
/// 곡 하나에 대해 밖에서 알아낸 것. 가사·의미와 따로 두는 이유는 이쪽이 비어 있어도
/// 가사는 멀쩡해야 하기 때문이다.
/// </summary>
/// <param name="CoverAt">
/// 커버를 찾아본 시각. <c>null</c>이면 아직 안 찾아본 것이고, 값이 있는데
/// <paramref name="CoverUrl"/>이 비어 있으면 <b>찾아봤지만 없었다</b>는 뜻이다(다시 부르지 않는다).
/// </param>
public sealed record SongLinks(
    string Key, string? CoverUrl = null, string? CoverSource = null,
    string? CoverAt = null, string? LastFmUrl = null)
{
    public bool CoverTried => !string.IsNullOrEmpty(CoverAt);
}

/// <summary>
/// 곡 상세가 보여 줄 Last.fm 상태. 계정을 연결하지 않았거나 조회가 실패하면 전부 꺼진 값이다 —
/// <b>모르는 것을 "좋아요 안 함"으로 그리면 안 된다</b>(꺼진 하트를 보고 다시 누르게 된다).
/// </summary>
public sealed record LoveState(bool Connected, bool Known, bool Loved)
{
    public static readonly LoveState NotConnected = new(false, false, false);
}

/// <summary>대시보드가 그리는 데 필요한 전부. 페이지 렌더러는 DB를 모른다(테스트 가능하도록).</summary>
public sealed record DashboardModel(
    ServerStats Stats,
    long DatabaseSizeBytes,
    HitRate Today,
    HitRate Week,
    IReadOnlyList<LookupRow> Recent,
    IReadOnlyList<MissRow> TopMisses,
    IReadOnlyList<DeviceRow> Devices,
    IReadOnlyList<DailyRow> Daily,
    IReadOnlyList<LookupRow> CleanedMatches,
    IReadOnlyList<SongRow> RecentUploads,
    IReadOnlyList<SongRow> WithoutTranslation,
    IReadOnlyList<SongRow> DuplicateCandidates,
    ServerHealth Health,
    IReadOnlyList<(string Name, string Value)> Diagnostics,
    MeaningSummary Meanings,
    /// <summary>지금 켜져 있는 의미 자료원 이름 — 무엇에 근거해 만들어지는지 화면에 드러낸다.</summary>
    IReadOnlyList<string> MeaningSources,
    string Csrf,
    /// <summary>광고로 표시해 차단한 제목들(되돌릴 수 있어야 하므로 화면에 보여 준다).</summary>
    IReadOnlyList<AdTitleRow>? AdTitles = null,
    /// <summary>Last.fm 계정 연결 상태 — 쓸 수 없는 구성이면 <c>null</c>이라 카드를 아예 안 그린다.</summary>
    LastFmLink? LastFm = null);

/// <summary>대시보드의 Last.fm 카드 — 연결한 아이디(없으면 미연결).</summary>
public sealed record LastFmLink(string? User);

/// <summary>대시보드의 "곡의 의미" 타일 — 만든 것 / 자료 없음 / 자료 부족 / 실패 + 아직 안 해 본 곡 수.</summary>
public sealed record MeaningSummary(
    int Ok, int NoSource, int Failed, int Pending, bool Enabled, int Insufficient = 0);

/// <summary>서버 상태(작은 인스턴스라 실제로 쓸모 있다).</summary>
public sealed record ServerHealth(TimeSpan Uptime, long WorkingSetBytes, long DiskFreeBytes, int RetentionDays);
