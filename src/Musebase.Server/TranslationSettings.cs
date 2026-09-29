using Musebase.Core.Translation;

namespace Musebase.Server;

/// <summary>
/// <see cref="LyricsStore"/>의 <c>translation_cache</c> 테이블을 코어 번역 서비스에 물려 주는 어댑터.
///
/// 코어의 <c>SqliteTranslationCache</c>를 그대로 쓰지 않는 이유는 <b>커넥션</b>이다 — 같은 DB
/// 파일에 두 번째 커넥션을 열면 <c>busy_timeout</c>이 없는 지금 구성에서 쓰기 충돌이
/// <c>SQLITE_BUSY</c>로 조용히 실패하고, 캐시 쓰기 실패는 다음 실행에서 같은 줄을 또 번역하는
/// 돈으로 샌다. 테이블 스키마는 코어와 같게 두었다.
/// </summary>
public sealed class StoreTranslationCache(LyricsStore store) : ITranslationCache
{
    public string? Get(string text, string targetLang) => store.GetTranslation(text, targetLang);

    public void Set(string text, string targetLang, string translation) =>
        store.SetTranslation(text, targetLang, translation);
}

/// <summary>
/// 번역 서비스를 <b>그때그때</b> 내어 주는 것. 구성이 화면에서 바뀌면 새 서비스가 나온다 —
/// 붙잡아 두면 옛 엔진이 계속 불린다. (테스트가 스텁 번역기를 물리는 자리이기도 하다.)
/// </summary>
public interface ITranslationServiceSource
{
    LyricsTranslationService Service { get; }

    /// <summary>
    /// 같은 구성의 번역기 자체. <see cref="Service"/>는 실패를 <b>조용히 삼켜</b> 0을 돌려주도록
    /// 만들어져 있어(재생 중에 오류창을 띄우지 않으려는 규칙) 왜 실패했는지 알 수 없다 —
    /// 일괄 작업은 시작 전에 한 번 직접 찔러 보고 이유를 사람에게 보여 준다.
    /// </summary>
    ITranslator? Translator { get; }

    /// <summary>
    /// 체인 구성원 각각. 찔러보기는 <b>멤버를 하나씩</b> 확인해야 한다 — 체인을 통째로 찌르면
    /// 보조가 살아 있을 때 주 엔진의 죽음이 가려진다(그 함수가 존재하는 이유가 바로 그 사고다).
    /// </summary>
    IReadOnlyList<(string EngineId, ITranslator Translator)> Members { get; }

    /// <summary>엔진과 키가 갖춰져 실제로 번역할 수 있는가.</summary>
    bool IsEnabled { get; }
}

/// <summary>
/// 지금 실제로 쓰는 번역 구성. <c>server.env</c> 위에 <b>DB에 저장된 값을 덮는다</b> —
/// <see cref="MeaningSettings"/>와 같은 구조이고 같은 이유다: 어느 엔진이 이 가사를 더 잘 옮기는지
/// 비교하려면 SSH로 파일을 고치고 재시작하는 것이 너무 무겁다. 바꾸면 <b>다음 실행부터</b> 반영된다.
///
/// ⚠ 저장한 API 키는 <b>DB에 평문으로 들어간다</b>(Last.fm 세션 키·Spotify 갱신 토큰과 같은 자리).
/// 백업 파일에 함께 담긴다는 뜻이므로, 키를 파일 밖에 두고 싶으면 <c>server.env</c>만 쓴다.
/// </summary>
public sealed class TranslationSettings(LyricsStore store, TranslationOptions environment, ITranslationCache cache)
    : ITranslationServiceSource
{
    public const string EngineSetting = "translate.engine";
    public const string LangSetting = "translate.lang";
    public const string DeeplKeySetting = "translate.deepl.key";
    public const string GoogleKeySetting = "translate.google.key";
    public const string MyMemoryEmailSetting = "translate.mymemory.email";
    public const string LibreEndpointSetting = "translate.libre.endpoint";
    public const string LibreKeySetting = "translate.libre.key";
    public const string OpenRouterKeySetting = "translate.openrouter.key";
    public const string OpenRouterModelSetting = "translate.openrouter.model";
    public const string FallbackSetting = "translate.fallback";

    private static readonly string[] AllSettings =
    [
        EngineSetting, LangSetting, DeeplKeySetting, GoogleKeySetting, MyMemoryEmailSetting,
        LibreEndpointSetting, LibreKeySetting, OpenRouterKeySetting, OpenRouterModelSetting,
        FallbackSetting,
    ];

    private readonly object _lock = new();
    private TranslationOptions? _current;
    private LyricsTranslationService? _service;
    private ITranslator? _translator;

    /// <summary><c>server.env</c>만 본 값 — 화면에서 "되돌리면" 여기로 간다.</summary>
    public TranslationOptions Environment => environment;

    /// <summary>지금 유효한 구성(환경변수 + DB 덮어쓰기).</summary>
    public TranslationOptions Current
    {
        get { lock (_lock) { return _current ??= Compose(); } }
    }

    /// <summary>
    /// 지금 구성으로 만든 서비스. 구성이 바뀌면 다시 만든다 — 캐시는 <b>공유 인스턴스</b>를
    /// 계속 물려 준다(서비스와 달리 만드는 값이 비싸고, 버릴 이유도 없다).
    /// </summary>
    public LyricsTranslationService Service
    {
        get
        {
            lock (_lock)
            {
                _current ??= Compose();
                return _service ??= _current.BuildService(cache);
            }
        }
    }

    /// <summary>번역기 자체(실패 이유를 보려는 곳에서 쓴다). 구성이 바뀌면 다시 만든다.</summary>
    public ITranslator? Translator
    {
        get
        {
            lock (_lock)
            {
                _current ??= Compose();
                return _translator ??= _current.BuildTranslator();
            }
        }
    }

    /// <summary>체인 구성원 각각(찔러보기용).</summary>
    public IReadOnlyList<(string EngineId, ITranslator Translator)> Members => Current.BuildMembers();

    /// <summary>엔진과 키가 갖춰져 실제로 번역할 수 있는가.</summary>
    public bool IsEnabled => Current.IsEnabled;

    /// <summary>DB에 값을 하나라도 저장해 뒀는가(화면에 "환경변수 사용 중"을 밝히기 위해).</summary>
    public bool Overridden => AllSettings.Any(n => !string.IsNullOrEmpty(store.GetSetting(n)));

    /// <summary>
    /// 화면에서 고친 값을 저장한다. <b>빈 칸은 "그대로 두기"</b>다(키를 마스킹해 보여 주므로
    /// 모델만 바꿀 때 긴 키를 다시 치게 하면 안 된다). 엔진을 <c>none</c>으로 두면 번역이 꺼진다.
    /// </summary>
    public void Save(
        string? engine, string? lang, string? deeplKey, string? googleKey, string? myMemoryEmail,
        string? libreEndpoint, string? libreKey, string? openRouterKey, string? openRouterModel,
        string? fallback = null)
    {
        lock (_lock)
        {
            Put(EngineSetting, engine);
            Put(LangSetting, lang is null ? null : TranslationOptions.NormalizeLang(lang));
            Put(DeeplKeySetting, deeplKey);
            Put(GoogleKeySetting, googleKey);
            Put(MyMemoryEmailSetting, myMemoryEmail);
            Put(LibreEndpointSetting, libreEndpoint);
            Put(LibreKeySetting, libreKey);
            Put(OpenRouterKeySetting, openRouterKey);
            Put(OpenRouterModelSetting, openRouterModel);
            // 폴백은 비밀이 아니라 화면에 그대로 보이는 값이다 — "빈 칸 = 유지" 규칙(키를 다시 치게
            // 하지 않으려는 것)이 여기엔 해당하지 않는다. 빈 값은 "폴백 없음"이라는 뜻이어야 한다.
            PutExact(FallbackSetting, fallback);
            Invalidate();
        }

        void PutExact(string name, string? value)
        {
            if (value is null) return;                       // 화면이 안 보낸 칸 — 손대지 않는다
            var trimmed = value.Trim();
            if (trimmed.Length == 0) store.DeleteSetting(name);
            else store.SetSetting(name, trimmed);
        }

        void Put(string name, string? value)
        {
            if (value is null) return;                       // 화면이 안 보낸 칸 — 손대지 않는다
            var trimmed = value.Trim();
            if (trimmed.Length == 0) return;                 // 빈 칸 — 기존 값 유지
            store.SetSetting(name, trimmed);
        }
    }

    /// <summary>DB 값을 전부 지워 <c>server.env</c> 상태로 되돌린다.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (var name in AllSettings) store.DeleteSetting(name);
            Invalidate();
        }
    }

    private void Invalidate()
    {
        _current = null;
        _service = null;
        _translator = null;
    }

    private TranslationOptions Compose()
    {
        string? Get(string name) => store.GetSetting(name) is { Length: > 0 } v ? v : null;

        return environment with
        {
            Engine = Get(EngineSetting) ?? environment.Engine,
            Lang = TranslationOptions.NormalizeLang(Get(LangSetting) ?? environment.Lang),
            DeeplApiKey = Get(DeeplKeySetting) ?? environment.DeeplApiKey,
            GoogleApiKey = Get(GoogleKeySetting) ?? environment.GoogleApiKey,
            MyMemoryEmail = Get(MyMemoryEmailSetting) ?? environment.MyMemoryEmail,
            LibreEndpoint = Get(LibreEndpointSetting) ?? environment.LibreEndpoint,
            LibreApiKey = Get(LibreKeySetting) ?? environment.LibreApiKey,
            OpenRouterApiKey = Get(OpenRouterKeySetting) ?? environment.OpenRouterApiKey,
            OpenRouterModel = Get(OpenRouterModelSetting) ?? environment.OpenRouterModel,
            Fallback = Get(FallbackSetting) ?? environment.Fallback,
        };
    }
}
