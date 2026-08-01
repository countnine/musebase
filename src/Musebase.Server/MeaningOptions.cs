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
    bool UseWikipedia,
    int BackfillLimit)
{
    /// <summary>
    /// `MUSEBASE_MEANING_ENGINE`(gemini|openrouter|none, 기본 none),
    /// `MUSEBASE_MEANING_LANG`(기본 ko), `MUSEBASE_GEMINI_API_KEY` / `MUSEBASE_GEMINI_MODEL`,
    /// `MUSEBASE_OPENROUTER_API_KEY` / `MUSEBASE_OPENROUTER_MODEL`,
    /// `MUSEBASE_GENIUS_TOKEN`, `MUSEBASE_LASTFM_KEY`, `MUSEBASE_MEANING_WIKIPEDIA`(0이면 끔),
    /// `MUSEBASE_MEANING_BACKFILL_LIMIT`(기본 50).
    /// </summary>
    public static MeaningOptions FromEnvironment()
    {
        static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var limit = int.TryParse(Env("MUSEBASE_MEANING_BACKFILL_LIMIT"), out var n)
            ? Math.Clamp(n, 1, 500) : 50;

        return new MeaningOptions(
            Engine: Env("MUSEBASE_MEANING_ENGINE") ?? MeaningWriterRegistry.None,
            Lang: Env("MUSEBASE_MEANING_LANG") ?? "ko",
            GeminiApiKey: Env("MUSEBASE_GEMINI_API_KEY"),
            GeminiModel: Env("MUSEBASE_GEMINI_MODEL"),
            OpenRouterApiKey: Env("MUSEBASE_OPENROUTER_API_KEY"),
            OpenRouterModel: Env("MUSEBASE_OPENROUTER_MODEL"),
            GeniusToken: Env("MUSEBASE_GENIUS_TOKEN"),
            LastFmKey: Env("MUSEBASE_LASTFM_KEY"),
            UseWikipedia: Env("MUSEBASE_MEANING_WIKIPEDIA") != "0",
            BackfillLimit: limit);
    }

    /// <summary>구성된 소스만 골라 서비스를 만든다. 키가 하나도 없으면 소스가 비어 꺼진 상태가 된다.</summary>
    public SongMeaningService BuildService()
    {
        var sources = new List<ISongMeaningSource>();
        if (!string.IsNullOrWhiteSpace(GeniusToken)) sources.Add(new GeniusSource(GeniusToken!));
        if (!string.IsNullOrWhiteSpace(LastFmKey)) sources.Add(new LastFmSource(LastFmKey!));
        if (UseWikipedia) sources.Add(new WikipediaSource());

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
