using Musebase.Core.Meaning;

namespace Musebase.Server;

/// <summary>
/// 지금 실제로 쓰는 의미 생성 구성. <c>server.env</c>의 값 위에 <b>DB에 저장된 값을 덮는다</b>.
///
/// 예전에는 <see cref="MeaningOptions.FromEnvironment"/>가 부팅 때 한 번 돌고 끝이라, 모델 하나를
/// 바꾸려면 서버에 들어가 파일을 고치고 재시작해야 했다 — 어느 모델이 이 곡을 더 잘 쓰는지
/// 비교해 보려는 작업에는 너무 무겁다. 이제 관리 화면에서 바꾸면 다음 생성부터 바로 적용된다.
///
/// <b>DB에 값이 없으면 환경변수 그대로다.</b> 화면에서 한 번도 저장하지 않은 서버는 동작이 이전과 같고,
/// [환경변수로 되돌리기]를 누르면 다시 그 상태가 된다.
///
/// ⚠ 저장한 API 키는 <b>DB에 평문으로 들어간다</b>(Last.fm 세션 키와 같은 자리). 백업 파일에
/// 함께 담긴다는 뜻이므로, 키를 파일 밖에 두고 싶으면 화면에 넣지 말고 <c>server.env</c>만 쓴다.
/// </summary>
public sealed class MeaningSettings(LyricsStore store, MeaningOptions environment)
{
    public const string EngineSetting = "meaning.engine";
    public const string GeminiKeySetting = "meaning.gemini.key";
    public const string GeminiModelSetting = "meaning.gemini.model";
    public const string OpenRouterKeySetting = "meaning.openrouter.key";
    public const string OpenRouterModelSetting = "meaning.openrouter.model";

    private static readonly string[] AllSettings =
        [EngineSetting, GeminiKeySetting, GeminiModelSetting, OpenRouterKeySetting, OpenRouterModelSetting];

    private readonly object _lock = new();
    private MeaningOptions? _current;
    private SongMeaningService? _service;

    /// <summary><c>server.env</c>만 본 값 — 화면에서 "되돌리면" 여기로 간다.</summary>
    public MeaningOptions Environment => environment;

    /// <summary>지금 유효한 구성(환경변수 + DB 덮어쓰기).</summary>
    public MeaningOptions Current
    {
        get { lock (_lock) { return _current ??= Compose(); } }
    }

    /// <summary>
    /// 지금 구성으로 만든 서비스. <b>구성이 바뀌면 다시 만든다</b> —
    /// 부팅 때 만든 것을 계속 들고 있으면 화면에서 모델을 바꿔도 옛 모델이 계속 불린다.
    /// </summary>
    public SongMeaningService Service
    {
        get
        {
            lock (_lock)
            {
                _current ??= Compose();
                return _service ??= _current.BuildService();
            }
        }
    }

    /// <summary>DB에 값을 하나라도 저장해 뒀는가(화면에 "환경변수 사용 중"을 밝히기 위해).</summary>
    public bool Overridden => AllSettings.Any(n => !string.IsNullOrEmpty(store.GetSetting(n)));

    /// <summary>
    /// 화면에서 고친 값을 저장한다. <b>빈 칸은 "그대로 두기"</b>다 — 키는 화면에 마스킹해서
    /// 보여 주므로, 모델만 바꾸려고 저장할 때마다 긴 키를 다시 치게 하면 안 된다.
    /// </summary>
    public void Save(string? engine, string? geminiKey, string? geminiModel,
                     string? openRouterKey, string? openRouterModel)
    {
        lock (_lock)
        {
            Put(EngineSetting, engine);
            Put(GeminiKeySetting, geminiKey);
            Put(GeminiModelSetting, geminiModel);
            Put(OpenRouterKeySetting, openRouterKey);
            Put(OpenRouterModelSetting, openRouterModel);
            Invalidate();
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
    }

    private MeaningOptions Compose()
    {
        string? Get(string name) => store.GetSetting(name) is { Length: > 0 } v ? v : null;

        return environment with
        {
            Engine = Get(EngineSetting) ?? environment.Engine,
            GeminiApiKey = Get(GeminiKeySetting) ?? environment.GeminiApiKey,
            GeminiModel = Get(GeminiModelSetting) ?? environment.GeminiModel,
            OpenRouterApiKey = Get(OpenRouterKeySetting) ?? environment.OpenRouterApiKey,
            OpenRouterModel = Get(OpenRouterModelSetting) ?? environment.OpenRouterModel,
        };
    }
}
