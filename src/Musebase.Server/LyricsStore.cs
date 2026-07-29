using Microsoft.Data.Sqlite;
using Musebase.Core.Search;

namespace Musebase.Server;

/// <summary>
/// 서버 저장소(SQLite). 클라이언트의 <see cref="LyricsCacheStore"/>와 달리 LRC를
/// **파싱·재직렬화하지 않고 문자열 그대로** 보관한다 — 서버를 통과할 때마다 원본이 미세하게
/// 달라지는 것을 막기 위해서다. 코어는 ①키 계산 ②느슨한 키 ③읽기 전용 파싱에만 쓴다.
///
/// 가족 규모(초당 수 건)이므로 단일 커넥션 + lock으로 충분하다(코어 캐시와 같은 패턴).
/// WAL을 켜 백업(`sqlite3 .backup`)이 쓰기 중에도 일관 스냅샷을 뜨게 한다.
/// </summary>
public sealed class LyricsStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private readonly string _dbPath;

    /// <summary>저장·비교에 쓰는 시각 포맷(ISO-8601 UTC). 문자열 비교로 정렬·범위 질의가 되도록 고정 폭.</summary>
    internal const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

    internal static string UtcNow() => DateTimeOffset.UtcNow.ToString(TimeFormat);

    public LyricsStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _dbPath = Path.GetFullPath(dbPath);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS lyrics (
                key        TEXT PRIMARY KEY,
                loose_key  TEXT NOT NULL,
                title      TEXT NOT NULL,
                artist     TEXT NOT NULL,
                lrc        TEXT NOT NULL,
                service    TEXT,
                origin     TEXT NOT NULL,
                langs      TEXT NOT NULL,
                line_count INTEGER NOT NULL,
                has_inline INTEGER NOT NULL,
                revision   INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_lyrics_loose ON lyrics(loose_key);
            """);
        Migrate();
    }

    /// <summary>
    /// 스키마 버전 마이그레이션. <c>PRAGMA user_version</c>으로 관리한다 —
    /// <c>CREATE TABLE IF NOT EXISTS</c>와 달리 <c>ALTER TABLE</c>은 재실행하면 실패하므로,
    /// 컬럼을 더할 일이 생기기 전에 버전 관리를 들여 둔다.
    /// 0 = lyrics만(v1 배포본), 1 = lookups(조회 기록) 추가.
    /// </summary>
    private void Migrate()
    {
        if (ScalarInt("PRAGMA user_version;") >= 1) return;

        Execute("""
            CREATE TABLE IF NOT EXISTS lookups (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                at     TEXT NOT NULL,     -- ISO-8601 UTC (lyrics.updated_at과 같은 포맷)
                title  TEXT NOT NULL,
                artist TEXT NOT NULL,
                result TEXT NOT NULL,     -- 'exact' | 'cleaned' | 'miss'
                key    TEXT,              -- 히트 시 맞은 행의 key, 미스면 NULL
                device TEXT NOT NULL,
                client TEXT               -- User-Agent 원문(진단용)
            );
            CREATE INDEX IF NOT EXISTS ix_lookups_at        ON lookups(at);
            CREATE INDEX IF NOT EXISTS ix_lookups_result_at ON lookups(result, at);
            """);
        Execute("PRAGMA user_version = 1;");
    }

    // ---- 키 계산 (클라이언트와 같은 코드를 쓴다) ----

    /// <summary>정확 키 — 클라이언트 로컬 캐시 키와 동일한 규칙.</summary>
    public static string ExactKey(string title, string artist) => LyricsCacheStore.MakeKey(title, artist);

    /// <summary>
    /// 느슨한 키 후보 — 기기마다 다른 메타데이터 표기를 흡수한다. 정확 키는 포함하지 않는다.
    ///
    /// 두 종류의 잡음을 다룬다:
    /// ① 제목·아티스트의 피처링·리마스터 표기(<see cref="SearchTermCleaner.Variants"/> — 공개 API라
    ///    코어 가시성을 건드리지 않는다),
    /// ② **아티스트 뒤에 붙는 앨범명** — Windows의 SMTC는 플레이어에 따라 아티스트를
    ///    "The Rolling Stones — Foreign Tongues"처럼 앨범까지 담아 보고하는데, Android는 아티스트만
    ///    보고한다. 이걸 흡수하지 않으면 같은 곡이 기기마다 다른 키로 갈린다(실제 캐시에서 확인됨).
    /// </summary>
    public static IReadOnlyList<string> LooseKeys(string title, string artist)
    {
        var exact = ExactKey(title, artist);
        var titles = new List<string> { title };
        var artists = new List<string> { artist };

        foreach (var variant in SearchTermCleaner.Variants(new SearchTerm(title, artist)))
        {
            if (variant.IsKeyword) continue;
            if (variant.Title is { Length: > 0 } t && !titles.Contains(t, StringComparer.OrdinalIgnoreCase)) titles.Add(t);
            if (variant.Artist is { } a && !artists.Contains(a, StringComparer.OrdinalIgnoreCase)) artists.Add(a);
        }

        // 아티스트에서 앨범 꼬리를 떼어 낸 형태도 후보에 넣는다.
        foreach (var a in artists.ToArray())
        {
            var stripped = StripAlbumSuffix(a);
            if (!stripped.Equals(a, StringComparison.OrdinalIgnoreCase)) artists.Add(stripped);
        }

        var keys = new List<string>();
        foreach (var t in titles)
        foreach (var a in artists)
        {
            var key = LyricsCacheStore.MakeKey(t, a);
            if (key != exact && !keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// 저장 시 붙일 대표 느슨한 키 — 가장 많이 정규화된 형태(정제 제목 + 앨범 꼬리를 뗀 정제 아티스트).
    /// 다른 기기가 이 최소 형태를 정확 키로 보내면 <c>loose_key</c> 조회로 맞는다.
    /// </summary>
    public static string PrimaryLooseKey(string title, string artist)
    {
        var cleanTitle = title;
        var cleanArtist = artist;
        foreach (var variant in SearchTermCleaner.Variants(new SearchTerm(title, artist)))
        {
            if (variant.IsKeyword) continue;
            if (variant.Title is { Length: > 0 } t) cleanTitle = t;
            if (variant.Artist is { Length: > 0 } a) cleanArtist = a;
            break; // 첫 변형이 가장 정제된 형태다
        }
        return LyricsCacheStore.MakeKey(cleanTitle, StripAlbumSuffix(cleanArtist));
    }

    /// <summary>
    /// "아티스트 — 앨범" 표기에서 앨범 꼬리를 떼어 낸다. 공백으로 감싼 em/en 대시만 대상으로 해
    /// "Jay-Z" 같은 이름은 건드리지 않는다(대시가 없으면 원본 그대로).
    /// </summary>
    public static string StripAlbumSuffix(string artist)
    {
        foreach (var separator in new[] { " — ", " – " })
        {
            var index = artist.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0) return artist[..index].Trim();
        }
        return artist;
    }

    // ---- 조회 ----

    /// <summary>정확 키 → 미스면 느슨한 키 순으로 조회한다. 없으면 null.</summary>
    public LyricsEntry? Get(string title, string artist)
    {
        lock (_lock)
        {
            var exact = ExactKey(title, artist);
            if (Read("key = $k", exact) is { } hit) return hit with { Match = LyricsEntry.MatchExact };

            // 저장본 쪽이 잡음 표기이고 질의가 깨끗한 경우("Love Story (Taylor's Version)" 저장 ↔ "Love Story" 질의):
            // 저장본의 loose_key가 질의의 정확 키와 같다.
            if (Read("loose_key = $k", exact) is { } byStoredLoose)
                return byStoredLoose with { Match = LyricsEntry.MatchCleaned };

            foreach (var loose in LooseKeys(title, artist))
            {
                // 정제 키로 저장된 행(자기 loose_key) 또는 정제 결과가 정확 키인 행 둘 다 본다.
                if (Read("loose_key = $k", loose) is { } byLoose) return byLoose with { Match = LyricsEntry.MatchCleaned };
                if (Read("key = $k", loose) is { } byKey) return byKey with { Match = LyricsEntry.MatchCleaned };
            }
            return null;
        }
    }

    private LyricsEntry? Read(string where, string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT key, title, artist, lrc, service, origin, langs, line_count, has_inline, revision, updated_at
            FROM lyrics WHERE {where} LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var langs = reader.GetString(6);
        return new LyricsEntry
        {
            Key = reader.GetString(0),
            Title = reader.GetString(1),
            Artist = reader.GetString(2),
            Lrc = reader.GetString(3),
            Service = reader.IsDBNull(4) ? null : reader.GetString(4),
            Origin = reader.GetString(5),
            Langs = langs.Length == 0 ? Array.Empty<string>() : langs.Split(','),
            LineCount = reader.GetInt32(7),
            HasInlineTimeTags = reader.GetInt32(8) != 0,
            Revision = reader.GetInt32(9),
            UpdatedAt = reader.GetString(10),
        };
    }

    // ---- 저장 ----

    /// <summary>
    /// 병합 정책을 적용해 업서트한다. 거부되면 null을 돌려주고 이유를 <paramref name="rejection"/>에 담는다.
    /// </summary>
    public LyricsEntry? Upsert(LyricsEntry incoming, string? updatedBy, out PutRejected? rejection)
    {
        rejection = null;
        var facts = LyricsFacts.From(incoming.Lrc);
        var origin = string.Equals(incoming.Origin, LyricsEntry.OriginUser, StringComparison.OrdinalIgnoreCase)
            ? LyricsEntry.OriginUser : LyricsEntry.OriginProvider;

        lock (_lock)
        {
            var key = ExactKey(incoming.Title, incoming.Artist);
            var current = Read("key = $k", key);
            (string, LyricsFacts)? existing = current is null
                ? null
                : (current.Origin, new LyricsFacts(current.LineCount ?? 0, current.HasInlineTimeTags ?? false,
                    current.Langs ?? Array.Empty<string>()));

            switch (MergePolicy.Evaluate(existing, origin, facts))
            {
                case MergePolicy.Decision.RejectUserEditProtected:
                    rejection = PutRejected.UserEditProtected;
                    return null;
                case MergePolicy.Decision.RejectPoorerContent:
                    rejection = PutRejected.PoorerContent;
                    return null;
            }

            var revision = (current?.Revision ?? 0) + 1;
            var updatedAt = UtcNow();
            var langs = string.Join(',', facts.Langs);

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lyrics (key, loose_key, title, artist, lrc, service, origin, langs,
                                    line_count, has_inline, revision, updated_at, updated_by)
                VALUES ($key, $loose, $title, $artist, $lrc, $service, $origin, $langs,
                        $lines, $inline, $rev, $at, $by)
                ON CONFLICT(key) DO UPDATE SET
                    loose_key = $loose, title = $title, artist = $artist, lrc = $lrc, service = $service,
                    origin = $origin, langs = $langs, line_count = $lines, has_inline = $inline,
                    revision = $rev, updated_at = $at, updated_by = $by;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$loose", PrimaryLooseKey(incoming.Title, incoming.Artist));
            cmd.Parameters.AddWithValue("$title", incoming.Title);
            cmd.Parameters.AddWithValue("$artist", incoming.Artist);
            cmd.Parameters.AddWithValue("$lrc", incoming.Lrc);
            cmd.Parameters.AddWithValue("$service", (object?)incoming.Service ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origin", origin);
            cmd.Parameters.AddWithValue("$langs", langs);
            cmd.Parameters.AddWithValue("$lines", facts.LineCount);
            cmd.Parameters.AddWithValue("$inline", facts.HasInlineTimeTags ? 1 : 0);
            cmd.Parameters.AddWithValue("$rev", revision);
            cmd.Parameters.AddWithValue("$at", updatedAt);
            cmd.Parameters.AddWithValue("$by", (object?)updatedBy ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            return incoming with
            {
                Key = key,
                Origin = origin,
                Langs = facts.Langs,
                LineCount = facts.LineCount,
                HasInlineTimeTags = facts.HasInlineTimeTags,
                Revision = revision,
                UpdatedAt = updatedAt,
                Match = LyricsEntry.MatchExact,
            };
        }
    }

    // ---- 부가 ----

    public ServerStats Stats()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), SUM(CASE WHEN langs <> '' THEN 1 ELSE 0 END), MAX(updated_at) FROM lyrics;
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return new ServerStats(0, 0, null);
            return new ServerStats(
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }

    /// <summary>
    /// 기존 클라이언트 캐시(`translations.db`의 `lyrics_cache`)를 통째로 흡수한다(시드용).
    /// 이미 있는 곡은 병합 정책을 그대로 거치므로 사용자 편집본을 덮지 않는다.
    /// </summary>
    public (int Imported, int Skipped) ImportLegacyCache(string legacyDbPath)
    {
        var imported = 0;
        var skipped = 0;

        using var src = new SqliteConnection($"Data Source={legacyDbPath};Mode=ReadOnly");
        src.Open();
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT key, lrc, service FROM lyrics_cache;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var lrc = reader.GetString(1);
            var service = reader.IsDBNull(2) ? null : reader.GetString(2);

            // 레거시 키는 "제목|아티스트"(정규화됨)라 원본 표기를 복원할 수 없다 —
            // 키에서 갈라 title/artist로 쓰면 같은 키가 다시 만들어진다.
            var parts = key.Split('|', 2);
            var title = parts[0];
            var artist = parts.Length > 1 ? parts[1] : "";

            var entry = new LyricsEntry
            {
                Title = title,
                Artist = artist,
                Lrc = lrc,
                Service = service,
                Origin = string.Equals(service, "사용자 편집", StringComparison.Ordinal)
                    ? LyricsEntry.OriginUser : LyricsEntry.OriginProvider,
            };
            if (Upsert(entry, updatedBy: "import", out _) is null) skipped++;
            else imported++;
        }
        return (imported, skipped);
    }

    // ---- 조회 기록(관리자 화면) ----

    /// <summary>
    /// 조회 1건 기록. 실패해도 조회 자체를 깨뜨리면 안 되므로 호출부에서 try/catch로 감싼다.
    /// <paramref name="result"/>는 "exact" | "cleaned" | "miss".
    /// </summary>
    public void LogLookup(string title, string artist, string result, string? key, string device, string? client)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lookups (at, title, artist, result, key, device, client)
                VALUES ($at, $title, $artist, $result, $key, $device, $client);
                """;
            cmd.Parameters.AddWithValue("$at", UtcNow());
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$artist", artist);
            cmd.Parameters.AddWithValue("$result", result);
            cmd.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$client", (object?)client ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>보존 기간이 지난 조회 기록을 지운다. 지운 행 수를 돌려준다.</summary>
    public int PruneLookups(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays)).ToString(TimeFormat);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM lookups WHERE at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>기간 내 결과별 건수(히트율 계산용).</summary>
    public HitRate HitRateSince(string sinceUtc)
    {
        int exact = 0, cleaned = 0, miss = 0;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT result, COUNT(*) FROM lookups WHERE at >= $since GROUP BY result;";
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt32(1);
                switch (reader.GetString(0))
                {
                    case LyricsEntry.MatchExact: exact = count; break;
                    case LyricsEntry.MatchCleaned: cleaned = count; break;
                    default: miss = count; break;
                }
            }
        }
        return new HitRate(exact, cleaned, miss);
    }

    /// <summary>최근 조회 기록(최신순).</summary>
    public IReadOnlyList<LookupRow> RecentLookups(int limit = 50)
    {
        var rows = new List<LookupRow>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT at, title, artist, result, key, device FROM lookups ORDER BY id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new LookupRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        }
        return rows;
    }

    /// <summary>기간 내 미스 상위 — 서버에 없는 곡(=채울 후보).</summary>
    public IReadOnlyList<MissRow> TopMisses(string sinceUtc, int limit = 50)
    {
        var rows = new List<MissRow>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT MAX(title), MAX(artist), COUNT(*) AS cnt, MAX(at) AS last_at, COUNT(DISTINCT device)
                FROM lookups WHERE result = 'miss' AND at >= $since
                GROUP BY lower(title), lower(artist)
                ORDER BY cnt DESC, last_at DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new MissRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetInt32(4)));
        }
        return rows;
    }

    /// <summary>기기별 조회·히트 수와 마지막 접속.</summary>
    public IReadOnlyList<DeviceRow> DeviceActivity(string sinceUtc)
    {
        var rows = new List<DeviceRow>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT device, COUNT(*), SUM(CASE WHEN result <> 'miss' THEN 1 ELSE 0 END), MAX(at)
                FROM lookups WHERE at >= $since GROUP BY device ORDER BY MAX(at) DESC;
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new DeviceRow(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        }
        return rows;
    }

    /// <summary>일별 히트/미스 건수(막대 그래프용, 날짜 오름차순).</summary>
    public IReadOnlyList<DailyRow> DailyHitRate(string sinceUtc)
    {
        var rows = new List<DailyRow>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT substr(at, 1, 10) AS day,
                       SUM(CASE WHEN result <> 'miss' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN result =  'miss' THEN 1 ELSE 0 END)
                FROM lookups WHERE at >= $since GROUP BY day ORDER BY day;
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new DailyRow(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        }
        return rows;
    }

    /// <summary>기간 내 느슨한 키로 맞은 조회 — 표기 차이가 실제로 흡수되는지 확인용.</summary>
    public IReadOnlyList<LookupRow> CleanedMatches(string sinceUtc, int limit = 50)
    {
        var rows = new List<LookupRow>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT at, title, artist, result, key, device FROM lookups
                WHERE result = 'cleaned' AND at >= $since ORDER BY id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new LookupRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        }
        return rows;
    }

    // ---- 목록·검색(관리자 화면) ----

    private const string SongColumns =
        "key, loose_key, title, artist, service, origin, langs, line_count, has_inline, revision, updated_at, updated_by";

    /// <summary>
    /// 제목·아티스트 부분 일치 검색(대소문자 무시). 질의가 비면 최근 갱신순 목록.
    /// 곡 수가 수백 규모라 LIKE 풀스캔으로 충분하다(`%…%`는 어차피 인덱스를 못 탄다).
    /// </summary>
    public IReadOnlyList<SongRow> Search(string? query, int limit = 100, int offset = 0)
    {
        var like = AdminQuery.ToLikePattern(query);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = like is null
                ? $"SELECT {SongColumns} FROM lyrics ORDER BY updated_at DESC LIMIT $limit OFFSET $offset;"
                : $"""
                   SELECT {SongColumns} FROM lyrics
                   WHERE lower(title) LIKE $like ESCAPE '\' OR lower(artist) LIKE $like ESCAPE '\'
                   ORDER BY updated_at DESC LIMIT $limit OFFSET $offset;
                   """;
            if (like is not null) cmd.Parameters.AddWithValue("$like", like);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            return ReadSongs(cmd);
        }
    }

    /// <summary>최근에 올라온 곡(대시보드용).</summary>
    public IReadOnlyList<SongRow> RecentUploads(int limit = 20)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SongColumns} FROM lyrics ORDER BY updated_at DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadSongs(cmd);
        }
    }

    /// <summary>번역이 하나도 없는 곡 — 일괄 사전번역 대상 후보.</summary>
    public IReadOnlyList<SongRow> WithoutTranslation(int limit = 200)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SongColumns} FROM lyrics WHERE langs = '' ORDER BY updated_at DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadSongs(cmd);
        }
    }

    /// <summary>
    /// 느슨한 키가 같은데 정확 키가 다른 행들 — 같은 곡이 표기 차이로 갈려 저장됐을 후보.
    /// 키 정규화가 실제로 먹는지 눈으로 확인하는 진단 목록이다.
    /// </summary>
    public IReadOnlyList<SongRow> DuplicateCandidates(int limit = 100)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SongColumns} FROM lyrics
                WHERE loose_key IN (SELECT loose_key FROM lyrics GROUP BY loose_key HAVING COUNT(*) > 1)
                ORDER BY loose_key, updated_at DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadSongs(cmd);
        }
    }

    private static List<SongRow> ReadSongs(SqliteCommand cmd)
    {
        var rows = new List<SongRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var langs = reader.GetString(6);
            rows.Add(new SongRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                langs.Length == 0 ? Array.Empty<string>() : langs.Split(','),
                reader.GetInt32(7), reader.GetInt32(8) != 0, reader.GetInt32(9),
                reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return rows;
    }

    /// <summary>정확 키로 1건(관리자 상세용, LRC 전문 포함). <c>Match</c>는 채우지 않는다.</summary>
    public LyricsEntry? GetByKey(string key)
    {
        lock (_lock) return Read("key = $k", key);
    }

    /// <summary>곡 1건 삭제(관리자 전용). 지워졌으면 true.</summary>
    public bool Delete(string key)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM lyrics WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>DB 파일 크기(WAL 포함).</summary>
    public long DatabaseSizeBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var info = new FileInfo(_dbPath + suffix);
                if (info.Exists) total += info.Length;
            }
            catch (Exception) { /* 크기 조회 실패는 무시 */ }
        }
        return total;
    }

    private int ScalarInt(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
