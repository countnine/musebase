namespace LyricsX.Core.Translation;

/// <summary>번역기 생성에 필요한 옵션(엔진별로 필요한 것만 사용).</summary>
public sealed record TranslatorOptions(
    string? DeeplApiKey = null,
    string? LibreEndpoint = null,
    string? LibreApiKey = null);

/// <summary>번역 엔진 설명자. 키 필요 여부·무료 여부(UI/기본값 판단)와 생성 팩토리.</summary>
public sealed record TranslatorDescriptor(
    string Id,
    string DisplayName,
    bool RequiresApiKey,
    bool IsFree,
    Func<TranslatorOptions, ITranslator?> Factory);

/// <summary>
/// 번역 엔진 레지스트리. 새 엔진은 <see cref="All"/>에 한 줄 추가로 편입된다.
/// 설정(<c>TranslationEngine</c>)으로 선택하고 <see cref="TranslatorOptions"/>로 키/엔드포인트를 주입한다.
/// 키 부족/미지원이면 팩토리가 null을 반환 → 번역 비활성(제공자 번역만).
/// </summary>
public static class TranslatorRegistry
{
    /// <summary>번역 비활성 식별자.</summary>
    public const string None = "none";

    /// <summary>LibreTranslate 기본 엔드포인트(설정으로 자체호스팅 인스턴스로 교체 가능).</summary>
    public const string DefaultLibreEndpoint = "https://libretranslate.com";

    public static IReadOnlyList<TranslatorDescriptor> All { get; } = new TranslatorDescriptor[]
    {
        new("libretranslate", "LibreTranslate (무료·무키)", RequiresApiKey: false, IsFree: true,
            o => new LibreTranslateTranslator(
                string.IsNullOrWhiteSpace(o.LibreEndpoint) ? DefaultLibreEndpoint : o.LibreEndpoint!,
                o.LibreApiKey)),
        new("deepl", "DeepL", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.DeeplApiKey) ? null : new DeeplTranslator(o.DeeplApiKey!)),
    };

    public static TranslatorDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>선택된 엔진의 번역기를 만든다. "none"/미지원/키 부족이면 null.</summary>
    public static ITranslator? Build(string id, TranslatorOptions options)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, None, StringComparison.OrdinalIgnoreCase))
            return null;
        return Find(id)?.Factory(options);
    }
}
