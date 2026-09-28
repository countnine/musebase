using Musebase.Core.Meaning;
using Musebase.Core.Translation;

namespace Musebase.Server;

/// <summary>
/// 서버가 가사를 번역할 때 쓰는 구성. 의미 생성의 <see cref="MeaningOptions"/>와 같은 모양이다
/// (환경변수가 바닥값, 관리 화면이 DB로 덮는다 — <see cref="TranslationSettings"/>).
///
/// <b>기본값은 "끔"이다.</b> 서버가 부팅만으로 외부 번역 API를 두드리기 시작하면 안 된다 —
/// 무료 한도든 유료 잔액이든 사람이 고른 뒤에 쓴다.
///
/// 대상 언어는 <b>대문자 DeepL식</b>(<c>KO</c>·<c>EN-US</c>·<c>ZH-HANT</c>)으로 보관한다.
/// 엔진별 코드 변환은 각 번역기가 안에서 하고, LRC 태그는 서비스가 소문자로 만든다
/// (<c>tr:ko</c>). 캐시 키에는 이 표기가 그대로 들어가므로 <b>클라이언트와 같은 대문자</b>여야
/// 같은 줄을 두 번 번역하지 않는다.
/// </summary>
public sealed record TranslationOptions(
    string Engine,
    string Lang,
    string? DeeplApiKey,
    string? GoogleApiKey,
    string? MyMemoryEmail,
    string? LibreEndpoint,
    string? LibreApiKey,
    string? OpenRouterApiKey,
    string? OpenRouterModel,
    int BatchLimit,
    int DelayMs,
    long CharBudget)
{
    /// <summary>
    /// `MUSEBASE_TRANSLATE_ENGINE`(레지스트리 id 또는 none — <b>기본 none</b>),
    /// `MUSEBASE_TRANSLATE_LANG`(기본 KO), `MUSEBASE_DEEPL_API_KEY`,
    /// `MUSEBASE_GOOGLE_TRANSLATE_KEY`, `MUSEBASE_MYMEMORY_EMAIL`,
    /// `MUSEBASE_LIBRETRANSLATE_ENDPOINT` / `_KEY`,
    /// `MUSEBASE_TRANSLATE_OPENROUTER_KEY` / `_MODEL`,
    /// `MUSEBASE_TRANSLATE_BATCH_LIMIT`(기본 30), `MUSEBASE_TRANSLATE_DELAY_MS`(기본 0),
    /// `MUSEBASE_TRANSLATE_CHAR_BUDGET`(기본 50000 — 확인 화면 상한 입력칸의 바닥값).
    ///
    /// OpenRouter 키를 의미 생성 쪽과 <b>따로 둔다</b> — 같이 쓰고 싶으면 같은 값을 두 번 넣는다.
    /// 한쪽 키를 조용히 물려 쓰면 "의미만 끄려고 키를 지웠는데 번역도 멈추는" 식으로 놀란다.
    /// </summary>
    public static TranslationOptions FromEnvironment()
    {
        static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var batch = int.TryParse(Env("MUSEBASE_TRANSLATE_BATCH_LIMIT"), out var n)
            ? Math.Clamp(n, 1, 5000) : 30;
        var delay = int.TryParse(Env("MUSEBASE_TRANSLATE_DELAY_MS"), out var d)
            ? Math.Clamp(d, 0, 60_000) : 0;
        var budget = long.TryParse(Env("MUSEBASE_TRANSLATE_CHAR_BUDGET"), out var b)
            ? Math.Clamp(b, 1, 100_000_000) : 50_000;

        return new TranslationOptions(
            Engine: Env("MUSEBASE_TRANSLATE_ENGINE") ?? TranslatorRegistry.None,
            Lang: NormalizeLang(Env("MUSEBASE_TRANSLATE_LANG")),
            DeeplApiKey: Env("MUSEBASE_DEEPL_API_KEY"),
            GoogleApiKey: Env("MUSEBASE_GOOGLE_TRANSLATE_KEY"),
            MyMemoryEmail: Env("MUSEBASE_MYMEMORY_EMAIL"),
            LibreEndpoint: Env("MUSEBASE_LIBRETRANSLATE_ENDPOINT"),
            LibreApiKey: Env("MUSEBASE_LIBRETRANSLATE_KEY"),
            OpenRouterApiKey: Env("MUSEBASE_TRANSLATE_OPENROUTER_KEY"),
            OpenRouterModel: Env("MUSEBASE_TRANSLATE_OPENROUTER_MODEL"),
            BatchLimit: batch,
            DelayMs: delay,
            CharBudget: budget);
    }

    /// <summary>대상 언어 표기를 대문자로 모은다(<c>ko</c>로 들어와도 캐시 키가 갈리지 않게).</summary>
    public static string NormalizeLang(string? lang) =>
        string.IsNullOrWhiteSpace(lang) ? "KO" : lang!.Trim().ToUpperInvariant();

    /// <summary>번역을 실제로 할 수 있는 구성인가(엔진을 골랐고 그 엔진에 필요한 키가 있다).</summary>
    public bool IsEnabled => BuildTranslator() is not null;

    /// <summary>고른 엔진의 설명자(알 수 없는 id·none이면 null).</summary>
    public TranslatorDescriptor? Descriptor =>
        string.Equals(Engine, TranslatorRegistry.None, StringComparison.OrdinalIgnoreCase)
            ? null : TranslatorRegistry.Find(Engine);

    /// <summary>화면에 쓸 엔진 이름(끔이면 "끔").</summary>
    public string EngineName => Descriptor?.Name ?? "끔";

    /// <summary>
    /// 지금 실제로 불릴 모델 이름 — OpenRouter만 해당한다(기계 번역기는 모델 개념이 없다).
    /// 비워 두면 번역기의 기본값이 쓰이는데, 화면에 빈칸으로 보이면 "설정 안 된 것"으로 읽힌다.
    /// </summary>
    public string EffectiveModel =>
        string.Equals(Engine, "openrouter", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(OpenRouterModel)
                ? OpenRouterTranslator.DefaultModel : OpenRouterModel!.Trim())
            : "";

    /// <summary>
    /// 코어 번역기에 넘길 옵션. <b>HttpClient는 의미 생성 쪽의 여유 있는 것</b>(4분)을 쓴다 —
    /// 기본값인 <c>LyricsHttp.Client</c>는 타임아웃 15초로, 재생 중 화면 응답성에 맞춘 값이라
    /// LLM 번역의 예산(OpenRouter 기본 60초)을 몰래 자른다.
    /// </summary>
    public TranslatorOptions ToTranslatorOptions() => new(
        DeeplApiKey: DeeplApiKey,
        LibreEndpoint: LibreEndpoint,
        LibreApiKey: LibreApiKey,
        MyMemoryEmail: MyMemoryEmail,
        GoogleApiKey: GoogleApiKey,
        OpenRouterApiKey: OpenRouterApiKey,
        OpenRouterModel: OpenRouterModel,
        Http: MeaningHttp.Client);

    /// <summary>번역기(엔진을 안 골랐거나 키가 없으면 null).</summary>
    public ITranslator? BuildTranslator() =>
        TranslatorRegistry.Build(Engine, ToTranslatorOptions());

    /// <summary>
    /// 지금 구성으로 만든 번역 서비스. 폴백은 두지 않는다 — 일괄 작업은 "이 엔진으로 돌린다"가
    /// 분명해야 비용과 품질을 판단할 수 있고, 무료 엔진으로 몰래 넘어가면 가사 수천 줄이
    /// 공개 서버로 나간다.
    /// </summary>
    public LyricsTranslationService BuildService(ITranslationCache? cache) =>
        new(BuildTranslator(), cache) { EngineId = Engine };

    /// <summary>
    /// 이 엔진을 일괄 작업에 쓸 때 사람에게 미리 알려야 하는 것. 없으면 null.
    /// </summary>
    public string? Warning => Engine.ToLowerInvariant() switch
    {
        "mymemory" =>
            "MyMemory는 줄마다 요청을 하나씩, 간격 없이 보냅니다(앱에서 한 곡을 번역하는 용도로 만든 "
            + "엔진입니다). 일괄 작업에 쓰면 무료 일일 한도를 곧바로 태웁니다 — 한 곡 확인용으로만 쓰세요.",
        "libretranslate" =>
            "LibreTranslate 공개 인스턴스는 2026년부터 유료 키가 필요합니다 — 자체 호스팅 주소를 넣으세요.",
        _ => null,
    };
}
