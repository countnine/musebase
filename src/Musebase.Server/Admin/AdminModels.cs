namespace Musebase.Server;

/// <summary>조회 기록 1건(관리자 화면 표시용).</summary>
public sealed record LookupRow(string At, string Title, string Artist, string Result, string? Key, string Device);

/// <summary>미스 상위 1건 — 서버에 없어서 각 기기가 직접 검색해야 했던 곡.</summary>
public sealed record MissRow(string Title, string Artist, int Count, string LastAt, int Devices);

/// <summary>기기별 활동.</summary>
public sealed record DeviceRow(string Device, int Lookups, int Hits, string LastAt);

/// <summary>일별 히트/미스(막대 그래프용).</summary>
public sealed record DailyRow(string Day, int Hits, int Misses);

/// <summary>곡 목록 1행(LRC 본문 제외 — 목록은 가볍게).</summary>
public sealed record SongRow(
    string Key, string LooseKey, string Title, string Artist, string? Service, string Origin,
    string[] Langs, int LineCount, bool HasInlineTimeTags, int Revision, string UpdatedAt, string? UpdatedBy);

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
    IReadOnlyList<(string Name, string Value)> Diagnostics);

/// <summary>서버 상태(작은 인스턴스라 실제로 쓸모 있다).</summary>
public sealed record ServerHealth(TimeSpan Uptime, long WorkingSetBytes, long DiskFreeBytes, int RetentionDays);
