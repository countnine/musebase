using System.Text.Json;
using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 곡 의미 기능의 서버 구성. 전부 환경변수에서 읽고, **키가 없으면 그냥 꺼진다** —
/// 가사 기능에는 어떤 영향도 주지 않는다.
/// </summary>
public sealed record MeaningOptions(
    string Engine,
    string Lang,
    string? GeminiApiKey,
    string? GeminiModel,
    string? OpenRouterApiKey,
    string? OpenRouterModel,
    string? GeniusToken,
    string? LastFmKey,
    string? MusixmatchKey,
    IReadOnlyList<string> Sources,
    int BackfillLimit,
    int BackfillDelayMs)
{
    /// <summary>
    /// 소스 id. <b>기본값에 musixmatch는 없다</b> — 그 자료는 사람이 쓴 해설이 아니라
    /// 기계가 가사를 분석한 결과라(<see cref="MusixmatchMeaningSource"/> 참고) 켤지 말지를
    /// 운영자가 직접 정해야 한다.
    /// </summary>
    public static readonly string[] DefaultSources = ["genius", "lastfm", "wikipedia"];

    /// <summary>
    /// `MUSEBASE_MEANING_ENGINE`(gemini|openrouter|none, 기본 none),
    /// `MUSEBASE_MEANING_LANG`(기본 ko), `MUSEBASE_GEMINI_API_KEY` / `MUSEBASE_GEMINI_MODEL`,
    /// `MUSEBASE_OPENROUTER_API_KEY` / `MUSEBASE_OPENROUTER_MODEL`,
    /// `MUSEBASE_GENIUS_TOKEN`, `MUSEBASE_LASTFM_KEY`, `MUSEBASE_MUSIXMATCH_KEY`,
    /// `MUSEBASE_MEANING_SOURCES`(쉼표 구분, 기본 `genius,lastfm,wikipedia`),
    /// `MUSEBASE_MEANING_WIKIPEDIA`(0이면 끔 — 예전 변수, 아래 설명),
    /// `MUSEBASE_MEANING_BACKFILL_LIMIT`(기본 50),
    /// `MUSEBASE_MEANING_BACKFILL_DELAY_MS`(기본 0 — 아래 설명).
    ///
    /// `MUSEBASE_MEANING_WIKIPEDIA=0`은 소스 목록이 생기기 전부터 쓰던 변수라 계속 받아 준다 —
    /// 목록을 직접 지정하지 않은 경우에만 기본값에서 위키피디아를 뺀다(직접 지정이 항상 이긴다).
    ///
    /// 백필 간격이 기본 0인 이유: 유료 티어는 분당 한도가 넉넉해 일부러 느리게 돌 이유가 없고,
    /// 429가 나더라도 백필이 그 자리에서 멈추고 **아무것도 저장하지 않으므로** 망가지지 않는다.
    /// Gemini 무료 티어(15 RPM)처럼 빡빡한 한도에서 끝까지 한 번에 돌리고 싶으면 4500 정도를 준다.
    /// </summary>
    public static MeaningOptions FromEnvironment()
    {
        static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var limit = int.TryParse(Env("MUSEBASE_MEANING_BACKFILL_LIMIT"), out var n)
            ? Math.Clamp(n, 1, 500) : 50;
        var delay = int.TryParse(Env("MUSEBASE_MEANING_BACKFILL_DELAY_MS"), out var d)
            ? Math.Clamp(d, 0, 60_000) : 0;

        var sources = ParseSources(Env("MUSEBASE_MEANING_SOURCES"), Env("MUSEBASE_MEANING_WIKIPEDIA"));

        return new MeaningOptions(
            Engine: Env("MUSEBASE_MEANING_ENGINE") ?? MeaningWriterRegistry.None,
            Lang: Env("MUSEBASE_MEANING_LANG") ?? "ko",
            GeminiApiKey: Env("MUSEBASE_GEMINI_API_KEY"),
            GeminiModel: Env("MUSEBASE_GEMINI_MODEL"),
            OpenRouterApiKey: Env("MUSEBASE_OPENROUTER_API_KEY"),
            OpenRouterModel: Env("MUSEBASE_OPENROUTER_MODEL"),
            GeniusToken: Env("MUSEBASE_GENIUS_TOKEN"),
            LastFmKey: Env("MUSEBASE_LASTFM_KEY"),
            MusixmatchKey: Env("MUSEBASE_MUSIXMATCH_KEY"),
            Sources: sources,
            BackfillLimit: limit,
            BackfillDelayMs: delay);
    }

    /// <summary>설정 문자열 → 소스 id 목록. 알 수 없는 이름은 무시한다(오타로 서버가 죽지 않게).</summary>
    public static IReadOnlyList<string> ParseSources(string? configured, string? legacyWikipedia)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .Where(s => DefaultSources.Contains(s) || s == "musixmatch")
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return legacyWikipedia == "0"
            ? DefaultSources.Where(s => s != "wikipedia").ToList()
            : DefaultSources.ToList();
    }

    /// <summary>Musixmatch 곡 페이지 주소를 찾아 주는 클라이언트(키가 없으면 꺼진 상태로 동작).</summary>
    public MusixmatchApi MusixmatchApi() => new(MusixmatchKey ?? "");

    /// <summary>
    /// 고른 소스 중 **키까지 있는 것만** 골라 서비스를 만든다.
    /// 하나도 남지 않으면 소스가 비어 기능이 꺼진 상태가 된다.
    /// </summary>
    public SongMeaningService BuildService()
    {
        var sources = new List<ISongMeaningSource>();
        foreach (var id in Sources)
        {
            switch (id)
            {
                case "genius" when !string.IsNullOrWhiteSpace(GeniusToken):
                    sources.Add(new GeniusSource(GeniusToken!)); break;
                case "lastfm" when !string.IsNullOrWhiteSpace(LastFmKey):
                    sources.Add(new LastFmSource(LastFmKey!)); break;
                case "wikipedia":
                    sources.Add(new WikipediaSource()); break;
                case "musixmatch" when !string.IsNullOrWhiteSpace(MusixmatchKey):
                    sources.Add(new MusixmatchMeaningSource(MusixmatchApi())); break;
            }
        }

        var writer = MeaningWriterRegistry.Build(Engine, new MeaningWriterOptions
        {
            GeminiApiKey = GeminiApiKey,
            GeminiModel = GeminiModel,
            OpenRouterApiKey = OpenRouterApiKey,
            OpenRouterModel = OpenRouterModel,
        });

        return new SongMeaningService(sources, writer);
    }
}

/// <summary>생성 결과를 저장 행으로 옮기고, 저장 행에서 출처 목록을 되꺼내는 변환.</summary>
public static class MeaningMapper
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static MeaningEntry ToEntry(string key, string title, string artist, string lang, SongMeaning result) =>
        new()
        {
            Key = key,
            Title = title,
            Artist = artist,
            Summary = result.Summary,
            Lang = lang,
            Sources = JsonSerializer.Serialize(result.Sources, Json),
            GeniusUrl = result.GeniusUrl,
            Engine = result.Engine,
            Model = result.Model,
            Status = result.Status,
            UpdatedAt = LyricsStore.UtcNow(),
        };

    /// <summary>저장된 원문 JSON에서 출처(이름·주소)만 뽑는다. 깨져 있으면 빈 목록.</summary>
    public static IReadOnlyList<MeaningAttribution> Attribution(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson)) return [];
        try
        {
            var sources = JsonSerializer.Deserialize<MeaningSource[]>(sourcesJson!, Json);
            return sources is null
                ? []
                : sources.Select(s => new MeaningAttribution(s.Name, s.Url)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
