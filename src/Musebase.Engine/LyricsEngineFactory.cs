using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Engine;

/// <summary>
/// 플랫폼 무관 엔진 구성값. 설정(App)에서 채워 팩토리에 넘긴다.
/// Windows/Android/서버가 같은 구성으로 코디네이터를 조립한다.
/// </summary>
public sealed record EngineConfig(
    IReadOnlyList<string> EnabledLyricsSources,
    string TranslationEngineId,
    TranslatorOptions TranslatorOptions,
    string TargetLanguage,
    bool ShowOnlyTargetTranslation,
    double ManualOffsetSeconds,
    string CacheDbPath);

/// <summary>
/// 코어 서비스(가사 검색·번역·캐시)를 조합해 <see cref="LyricsCoordinator"/>를 만드는
/// 단일 조립 지점. Program의 뚱뚱한 배선을 대체하고, 동일 조합을 다른 플랫폼이 재사용한다.
/// 소스/엔진은 레지스트리에서 구성값에 따라 선택된다.
/// </summary>
public static class LyricsEngineFactory
{
    /// <summary>구성·캐시로 번역 서비스를 만든다(엔진/키 변경 시 재구성용으로도 사용).</summary>
    public static LyricsTranslationService BuildTranslation(EngineConfig config, ITranslationCache cache) =>
        new(TranslatorRegistry.Build(config.TranslationEngineId, config.TranslatorOptions), cache);

    /// <summary>재생 소스·디스패처·구성으로 완전 배선된 코디네이터를 만든다.</summary>
    public static LyricsCoordinator Create(
        INowPlayingSource source,
        IEngineDispatcher dispatcher,
        EngineConfig config,
        ITranslationCache translationCache,
        Action<string>? log = null) =>
        new(source, dispatcher, new LyricsSearchService(LyricsSourceRegistry.Build(config.EnabledLyricsSources)))
        {
            Translation = BuildTranslation(config, translationCache),
            Cache = new LyricsCacheStore(config.CacheDbPath),
            TargetLanguage = config.TargetLanguage,
            ShowOnlyTargetTranslation = config.ShowOnlyTargetTranslation,
            ManualOffsetSeconds = config.ManualOffsetSeconds,
            Log = log,
        };
}
