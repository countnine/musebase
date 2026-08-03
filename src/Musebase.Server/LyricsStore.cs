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
    /// 0 = lyrics만(v1 배포본), 1 = lookups(조회 기록), 2 = meanings(곡의 의미).
    /// </summary>
    private void Migrate()
    {
        var version = ScalarInt("PRAGMA user_version;");

        if (version < 1)
        {
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

        if (version < 2)
        {
            // 가사와 1:1(같은 key). 별도 테이블인 이유는 의미가 없어도 가사는 멀쩡해야 하고,
            // 재생성이 가사 revision을 건드리면 안 되기 때문이다.
            Execute("""
                CREATE TABLE IF NOT EXISTS meanings (
                    key        TEXT PRIMARY KEY,   -- lyrics.key와 같은 규칙
                    title      TEXT NOT NULL,
                    artist     TEXT NOT NULL,
                    summary    TEXT,               -- 생성된 대상 언어 문단
                    lang       TEXT NOT NULL,
                    sources    TEXT NOT NULL,      -- JSON 배열: [{name,url,text}]
                    genius_url TEXT,
                    engine     TEXT,
                    model      TEXT,               -- 재생성 판단용
                    status     TEXT NOT NULL,      -- 'ok' | 'no-source' | 'failed'
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_meanings_status ON meanings(status);
                """);
            Execute("PRAGMA user_version = 2;");
        }

        if (version < 3)
        {
            // 곡 페이지 주소는 공식 API로 확인한 것만 저장한다(규칙으로 만든 주소는 다른 곡으로 간다).
            Execute("ALTER TABLE meanings ADD COLUMN musixmatch_url TEXT;");
            Execute("PRAGMA user_version = 3;");
        }

        if (version < 4)
        {
            // "자료가 부족해 파악하기 어렵다"는 답도 글자가 있다는 이유로 ok로 저장돼 있었다.
            // 이미 쌓인 것까지 다시 갈라 준다 — 안 그러면 통계가 계속 부풀어 있고, 앱에는
            // 그 문장이 곡 해설이라며 뜬다. 판정은 생성 때와 **같은 함수**를 쓴다.
            ReclassifyInsufficient();
            Execute("PRAGMA user_version = 4;");
        }

        if (version < 5)
        {
            // 느슨한 키 규칙이 바뀌었다(공동 아티스트를 대표 한 명으로 줄인다).
            // 기존 행을 다시 계산하지 않으면 예전에 갈린 곡들이 영영 서로를 못 찾는다.
            RecomputeLooseKeys();
            Execute("PRAGMA user_version = 5;");
        }
    }

    /// <summary>모든 행의 <c>loose_key</c>를 지금 규칙으로 다시 계산한다(행은 합치지 않는다).</summary>
    private void RecomputeLooseKeys()
    {
        var rows = new List<(string Key, string Title, string Artist)>();
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT key, title, artist FROM lyrics;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var (key, title, artist) in rows)
        {
            using var update = _conn.CreateCommand();
            update.CommandText = "UPDATE lyrics SET loose_key = $loose WHERE key = $k;";
            update.Parameters.AddWithValue("$loose", PrimaryLooseKey(title, artist));
            update.Parameters.AddWithValue("$k", key);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 병합 규칙을 우회해 행을 그대로 넣는다 — <b>테스트 전용</b>.
    /// 예전 규칙으로 갈려 저장된 형제 행을 재현할 때 쓴다(운영 경로에서는 쓰지 않는다).
    /// </summary>
    public void UpsertRawForTest(string key, string looseKey, string title, string artist, string lrc)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO lyrics
                    (key, loose_key, title, artist, lrc, service, origin, langs,
                     line_count, has_inline, revision, updated_at, updated_by)
                VALUES ($key, $loose, $title, $artist, $lrc, 'LRCLIB', 'provider', '',
                        2, 0, 1, $at, 'test');
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$loose", looseKey);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$artist", artist);
            cmd.Parameters.AddWithValue("$lrc", lrc);
            cmd.Parameters.AddWithValue("$at", UtcNow());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>테스트에서 마이그레이션을 다시 돌려 보기 위한 것. 운영 경로에서는 쓰지 않는다.</summary>
    public void SetUserVersionForTest(int version)
    {
        lock (_lock) Execute($"PRAGMA user_version = {version};");
    }

    /// <summary>이미 저장된 `ok` 행 중 "자료 부족" 고백을 골라 상태를 고친다.</summary>
    private void ReclassifyInsufficient()
    {
        var targets = new List<string>();
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT key, summary FROM meanings WHERE status = 'ok' AND summary IS NOT NULL;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (Musebase.Core.Meaning.MeaningVerdict.IsInsufficient(reader.GetString(1)))
                    targets.Add(reader.GetString(0));
        }

        foreach (var key in targets)
        {
            using var update = _conn.CreateCommand();
            update.CommandText = "UPDATE meanings SET status = 'insufficient' WHERE key = $k;";
            update.Parameters.AddWithValue("$k", key);
            update.ExecuteNonQuery();
        }
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

        // 앨범 꼬리를 뗀 형태와, 공동 아티스트를 대표 한 명으로 줄인 형태도 후보에 넣는다
        // (구분자만 다른 표기 — "A/B" ↔ "A, B" — 를 흡수한다).
        foreach (var a in artists.ToArray())
        {
            foreach (var candidate in new[] { StripAlbumSuffix(a), LeadArtist(a) })
                if (!artists.Contains(candidate, StringComparer.OrdinalIgnoreCase)) artists.Add(candidate);
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
        return LyricsCacheStore.MakeKey(cleanTitle, LeadArtist(cleanArtist));
    }

    /// <summary>
    /// 느슨한 키에 쓸 <b>대표 아티스트 한 명</b>. 앨범 꼬리를 떼고 공동 아티스트도 첫 명만 남긴다.
    ///
    /// 실측으로 걸린 문제: 같은 폰이 같은 곡을 어떤 날은
    /// <c>"Lady Gaga/Bradley Cooper"</c>, 어떤 날은 <c>"Lady Gaga, Bradley Cooper"</c>로 보고했다.
    /// 구분자 하나가 달라 두 행으로 갈렸고, 한쪽에만 붙은 의미가 다른 쪽에서는 보이지 않았다.
    /// 제목이 같고 대표 아티스트가 같으면 사실상 같은 곡이므로, 여기까지 줄여 흡수한다
    /// (정확 키가 먼저 시도되므로 이건 어디까지나 <b>폴백</b>이다).
    /// </summary>
    public static string LeadArtist(string artist)
    {
        var stripped = StripAlbumSuffix(artist);
        var names = Musebase.Core.Meaning.ArtistNames.All(stripped);
        return names.Count > 0 ? names[0] : stripped;
    }

    /// <summary>
    /// 아티스트 뒤에 붙은 문맥 꼬리를 떼어 낸다. 두 가지 실측 형태를 다룬다 —
    /// Windows SMTC의 "아티스트 — 앨범", Spotify Android의 "아티스트 • 스마트셔플 추천".
    /// 공백으로 감싼 구분자만 대상으로 해 "Jay-Z" 같은 이름은 건드리지 않는다
    /// (구분자가 없으면 원본 그대로).
    /// </summary>
    public static string StripAlbumSuffix(string artist)
    {
        foreach (var separator in new[] { " — ", " – ", " • ", " · " })
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
        lock (_lock) return Locate(title, artist);
    }

    /// <summary>
    /// <see cref="Get"/>의 본체(락 안에서 호출). 저장(<see cref="Upsert"/>)도 같은 규칙을 써야
    /// **조회로는 맞는데 저장은 새 행을 만드는** 비대칭이 생기지 않는다.
    /// </summary>
    private LyricsEntry? Locate(string title, string artist)
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

            // 정확 키에 없으면 조회와 같은 규칙(느슨한 키)으로 한 번 더 찾는다. 기기마다 메타데이터
            // 표기가 달라(Windows는 아티스트에 앨범명이 붙는 등) 같은 곡이 두 행으로 갈리는 것을 막는다.
            // 찾으면 그 행을 갱신하되 key/제목/아티스트/loose_key는 **먼저 저장된 표기를 유지**한다 —
            // 여기서 바꾸면 원래 기기의 정확 키 조회가 깨진다.
            LyricsEntry? mergedInto = null;
            if (current is null
                && Locate(incoming.Title, incoming.Artist) is { Key: { Length: > 0 } existingKey } found)
            {
                mergedInto = found;
                current = found;
                key = existingKey;
            }

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
            cmd.CommandText = mergedInto is not null
                ? """
                  UPDATE lyrics SET
                      lrc = $lrc, service = $service, origin = $origin, langs = $langs,
                      line_count = $lines, has_inline = $inline,
                      revision = $rev, updated_at = $at, updated_by = $by
                  WHERE key = $key;
                  """
                : """
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
                // 합쳐 넣은 경우 응답은 실제 저장된 표기를 그대로 알린다(요청 표기가 아니라).
                Title = mergedInto?.Title ?? incoming.Title,
                Artist = mergedInto?.Artist ?? incoming.Artist,
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

    // ---- 곡의 의미 ----

    /// <summary>
    /// 저장된 의미를 찾는다. **가사와 같은 해석기(<see cref="Locate"/>)로 키를 정한다** —
    /// 가사가 느슨한 키로 맞는 곡은 의미도 같이 맞아야 한다.
    /// </summary>
    public MeaningEntry? GetMeaning(string title, string artist)
    {
        lock (_lock)
        {
            var found = Locate(title, artist);
            var key = found?.Key ?? ExactKey(title, artist);
            if (ReadMeaning(key) is { } direct) return direct;

            // 같은 곡이 표기 차이로 두 행에 갈려 있고 의미가 **한쪽에만** 붙어 있을 수 있다
            // (실측: "Lady Gaga/Bradley Cooper"와 "Lady Gaga, Bradley Cooper"). 가사가 맞았는데
            // 의미만 비는 상태는 만들지 않는다 — 같은 느슨한 키를 쓰는 형제 행까지 살펴본다.
            return ReadMeaningByLooseGroup(PrimaryLooseKey(title, artist))
                ?? (found is null ? null : ReadMeaningByLooseGroup(PrimaryLooseKey(found.Title, found.Artist)));
        }
    }

    /// <summary>관리자 화면처럼 이미 key를 아는 곳에서 쓴다.</summary>
    public MeaningEntry? GetMeaningByKey(string key)
    {
        lock (_lock) return ReadMeaning(key);
    }

    /// <summary>
    /// 같은 느슨한 키를 쓰는 행들 중 의미가 붙은 것을 찾는다.
    /// 쓸 수 있는 의미(<c>ok</c>)를 먼저 고른다 — "자료 부족" 행이 진짜 의미를 가릴 이유가 없다.
    /// </summary>
    private MeaningEntry? ReadMeaningByLooseGroup(string looseKey)
    {
        if (looseKey.Length == 0) return null;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.key FROM meanings m
            JOIN lyrics l ON l.key = m.key
            WHERE l.loose_key = $loose
            ORDER BY CASE WHEN m.status = 'ok' THEN 0 ELSE 1 END, m.updated_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$loose", looseKey);
        return cmd.ExecuteScalar() is string key ? ReadMeaning(key) : null;
    }

    private MeaningEntry? ReadMeaning(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, title, artist, summary, lang, sources, genius_url, engine, model, status,
                   updated_at, musixmatch_url
            FROM meanings WHERE key = $k LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new MeaningEntry
        {
            Key = reader.GetString(0),
            Title = reader.GetString(1),
            Artist = reader.GetString(2),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            Lang = reader.GetString(4),
            Sources = reader.GetString(5),
            GeniusUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            Engine = reader.IsDBNull(7) ? null : reader.GetString(7),
            Model = reader.IsDBNull(8) ? null : reader.GetString(8),
            Status = reader.GetString(9),
            UpdatedAt = reader.GetString(10),
            MusixmatchUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
        };
    }

    /// <summary>의미를 저장한다(같은 key면 덮어쓴다 — 재생성이 정상 경로다).</summary>
    public void UpsertMeaning(MeaningEntry entry)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO meanings (key, title, artist, summary, lang, sources, genius_url,
                                      engine, model, status, updated_at, musixmatch_url)
                VALUES ($key, $title, $artist, $summary, $lang, $sources, $genius,
                        $engine, $model, $status, $at, $mxm)
                ON CONFLICT(key) DO UPDATE SET
                    title = $title, artist = $artist, summary = $summary, lang = $lang,
                    sources = $sources, genius_url = $genius, engine = $engine, model = $model,
                    status = $status, updated_at = $at,
                    -- 이번에 못 찾았다고 지난번에 확인한 주소를 지우지 않는다.
                    musixmatch_url = COALESCE($mxm, musixmatch_url);
                """;
            cmd.Parameters.AddWithValue("$key", entry.Key);
            cmd.Parameters.AddWithValue("$title", entry.Title);
            cmd.Parameters.AddWithValue("$artist", entry.Artist);
            cmd.Parameters.AddWithValue("$summary", (object?)entry.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lang", entry.Lang);
            cmd.Parameters.AddWithValue("$sources", entry.Sources);
            cmd.Parameters.AddWithValue("$genius", (object?)entry.GeniusUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$engine", (object?)entry.Engine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", (object?)entry.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", entry.Status);
            cmd.Parameters.AddWithValue("$at", entry.UpdatedAt);
            cmd.Parameters.AddWithValue("$mxm", (object?)entry.MusixmatchUrl ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 아직 의미가 없는 곡(백필 대상). 이미 시도해 본 곡은 제외한다 —
    /// 실패·자료없음도 행이 남으므로 백필을 다시 눌러도 같은 곡을 무한히 재시도하지 않는다.
    /// </summary>
    public IReadOnlyList<(string Key, string Title, string Artist)> SongsWithoutMeaning(int limit)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT l.key, l.title, l.artist FROM lyrics l
                LEFT JOIN meanings m ON m.key = l.key
                WHERE m.key IS NULL
                ORDER BY l.updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, string, string)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }
    }

    /// <summary>대시보드 타일용. `자료 부족`은 글자는 있지만 의미가 아니므로 따로 센다.</summary>
    public (int WithMeaning, int NoSource, int Failed, int Insufficient) MeaningStats()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                  SUM(CASE WHEN status = 'ok' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status = 'no-source' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status = 'insufficient' THEN 1 ELSE 0 END)
                FROM meanings;
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (0, 0, 0, 0);
            int At(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            return (At(0), At(1), At(2), At(3));
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

    /// <summary>
    /// 최근 <paramref name="sinceUtc"/> 이후에 **다른 기기**가 같은 제목을 미스했는가.
    /// 참이면 그 기기가 지금 이 곡을 찾아 번역하고 있을 가능성이 높다 — 두 번째 기기가
    /// 번역을 양보하도록 GET 응답에 힌트를 실어 준다(`contracts/lyrics-api.md`의 "번역 양보").
    ///
    /// 아티스트가 아니라 **제목만** 비교한다. 기기마다 아티스트 표기가 갈리는 것이 애초의 문제라
    /// (`Phoenix` ↔ `Phoenix • 스마트셔플 추천`) 제목 쪽이 훨씬 안정적이다.
    /// </summary>
    public bool RecentlyMissedByOther(string title, string device, string sinceUtc)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT 1 FROM lookups
                WHERE result = 'miss' AND at >= $since AND device <> $device
                  AND lower(title) = lower($title)
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$title", title);
            using var reader = cmd.ExecuteReader();
            return reader.Read();
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

    /// <summary>
    /// 최근 조회 기록(최신순).
    ///
    /// 미스였던 행은 <c>key</c>가 비어 있지만 <b>그 뒤에 곡이 올라왔을 수 있다.</b>
    /// 그래서 표시 시점에 다시 찾아 키를 채운다 — 화면에서 곡으로 넘어갈 수 있게 하기 위한 것이고,
    /// <c>result</c>는 그대로 둔다(그때 미스였던 것은 사실이므로 기록을 바꾸면 안 된다).
    /// </summary>
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

            for (var i = 0; i < rows.Count; i++)
                if (string.IsNullOrEmpty(rows[i].Key))
                    rows[i] = rows[i] with { Key = Locate(rows[i].Title, rows[i].Artist)?.Key };
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

            // 미스로 기록됐어도 지금은 서버에 있을 수 있다 — 있으면 바로 열어 볼 수 있게 키를 붙인다.
            for (var i = 0; i < rows.Count; i++)
                rows[i] = rows[i] with { Key = Locate(rows[i].Title, rows[i].Artist)?.Key };
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
    /// 목록 조회는 의미 상태를 항상 함께 읽는다 — 검색 결과에 "의미" 열을 보여 주기 위해서다.
    /// <c>meanings.key</c>는 저장할 때 <see cref="Locate"/>가 정한 키(=<c>lyrics.key</c>)라
    /// 별도 해석 없이 그대로 조인하면 된다.
    /// </summary>
    private const string SongSelect =
        "SELECT l.key, l.loose_key, l.title, l.artist, l.service, l.origin, l.langs, l.line_count, "
        + "l.has_inline, l.revision, l.updated_at, l.updated_by, m.status "
        + "FROM lyrics l LEFT JOIN meanings m ON m.key = l.key";

    /// <summary>검색 화면의 의미 필터.</summary>
    public const string MeaningFilterOk = "ok";
    public const string MeaningFilterNone = "none";

    private static string MeaningWhere(string? filter) => filter switch
    {
        MeaningFilterOk => " m.status = 'ok' ",
        MeaningFilterNone => " (m.status IS NULL OR m.status <> 'ok') ",
        _ => "",
    };

    /// <summary>
    /// 제목·아티스트 부분 일치 검색(대소문자 무시). 질의가 비면 최근 갱신순 목록.
    /// 곡 수가 수백 규모라 LIKE 풀스캔으로 충분하다(`%…%`는 어차피 인덱스를 못 탄다).
    /// </summary>
    public IReadOnlyList<SongRow> Search(
        string? query, int limit = 100, int offset = 0, string? meaning = null)
    {
        var like = AdminQuery.ToLikePattern(query);
        var conditions = new List<string>();
        if (like is not null)
            conditions.Add(@" (lower(l.title) LIKE $like ESCAPE '\' OR lower(l.artist) LIKE $like ESCAPE '\') ");
        if (MeaningWhere(meaning) is { Length: > 0 } meaningWhere) conditions.Add(meaningWhere);

        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);

        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                $"{SongSelect}{where} ORDER BY l.updated_at DESC LIMIT $limit OFFSET $offset;";
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
            cmd.CommandText = $"{SongSelect} ORDER BY l.updated_at DESC LIMIT $limit;";
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
            cmd.CommandText = $"{SongSelect} WHERE l.langs = '' ORDER BY l.updated_at DESC LIMIT $limit;";
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
                {SongSelect}
                WHERE l.loose_key IN (SELECT loose_key FROM lyrics GROUP BY loose_key HAVING COUNT(*) > 1)
                ORDER BY l.loose_key, l.updated_at DESC LIMIT $limit;
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
                reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
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
