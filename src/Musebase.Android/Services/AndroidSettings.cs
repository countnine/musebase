using Android.Content;
using Musebase.Core.Translation;

namespace Musebase.Android.Services;

/// <summary>
/// 앱 설정 저장소 — Android <see cref="ISharedPreferences"/>(앱 private) 래퍼.
///
/// 저장 위치는 앱 private 영역(<c>getSharedPreferences("musebase", MODE_PRIVATE)</c>)이므로
/// 타 앱에서 접근할 수 없다. 다만 이는 **앱 private 저장일 뿐 디스크 암호화가 아니다** —
/// 루팅된 기기나 백업 추출로는 평문 노출이 가능하다(Windows판의 DPAPI 암호화와 다르다).
/// DeepL API 키 같은 민감정보는 이 한계를 감안한다(더 강한 보호가 필요하면
/// AndroidX Security의 EncryptedSharedPreferences 도입을 고려 — 추가 패키지/복잡도 필요).
///
/// 직렬화 키(<c>TranslationEngine</c> 등)는 플랫폼 간 정렬을 위해 영어 식별자로 유지한다
/// (Windows AppSettings와 동일 규칙 — UI 문구만 현지화).
/// </summary>
public sealed class AndroidSettings
{
    private const string PrefsName = "musebase";
    private const string KeyTranslationEngine = "TranslationEngine";
    private const string KeyDeeplApiKey = "DeeplApiKey";
    private const string KeyGoogleApiKey = "GoogleApiKey";
    private const string KeyTargetLanguage = "TargetLanguage";
    private const string KeyTranslationFallbackToFree = "TranslationFallbackToFree";
    private const string KeyApiTranslationEnabled = "ApiTranslationEnabled";
    private const string KeyPlaybackSource = "PlaybackSource";
    private const string KeyIncludeVideoApps = "IncludeVideoApps";

    private readonly ISharedPreferences _prefs;

    public AndroidSettings(Context context)
    {
        _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
    }

    /// <summary>
    /// 선택된 번역 엔진 id(<see cref="TranslatorRegistry"/>). 기본은 무키 무료
    /// <see cref="TranslatorRegistry.DefaultFreeEngine"/>("mymemory"). "none"이면 번역 끔.
    /// </summary>
    public string TranslationEngine
    {
        get => _prefs.GetString(KeyTranslationEngine, TranslatorRegistry.DefaultFreeEngine)!;
        set => Put(KeyTranslationEngine, value);
    }

    /// <summary>DeepL API 키(선택). 앱 private 저장이며 디스크 암호화는 아니다(클래스 주석 참고).</summary>
    public string? DeeplApiKey
    {
        get => NullIfBlank(_prefs.GetString(KeyDeeplApiKey, null));
        set => Put(KeyDeeplApiKey, value);
    }

    /// <summary>Google Cloud Translation API 키(선택). DeepL 키와 같은 저장 한계를 갖는다.</summary>
    public string? GoogleApiKey
    {
        get => NullIfBlank(_prefs.GetString(KeyGoogleApiKey, null));
        set => Put(KeyGoogleApiKey, value);
    }

    /// <summary>엔진 id별 API 키 조회(설정 화면이 선택한 엔진의 키를 따라 보여주는 데 쓴다).</summary>
    public string? GetTranslationApiKey(string engineId) => engineId?.ToLowerInvariant() switch
    {
        "deepl" => DeeplApiKey,
        "google" => GoogleApiKey,
        _ => null,
    };

    /// <summary>엔진 id별 API 키 저장(빈 값은 저장소가 제거 처리).</summary>
    public void SetTranslationApiKey(string engineId, string? key)
    {
        switch (engineId?.ToLowerInvariant())
        {
            case "deepl": DeeplApiKey = key; break;
            case "google": GoogleApiKey = key; break;
        }
    }

    /// <summary>번역 대상 언어(DeepL target_lang 코드). 비면 기기 로케일 기본값을 쓴다.</summary>
    public string? TargetLanguage
    {
        get => NullIfBlank(_prefs.GetString(KeyTargetLanguage, null));
        set => Put(KeyTargetLanguage, value);
    }

    /// <summary>
    /// 재생 소스 선택. "auto" = 자동 감지, 그 외 = 특정 앱 패키지로 고정
    /// (Windows AppSettings.PlaybackSource와 같은 의미 — 그쪽은 SourceAppUserModelId).
    /// </summary>
    public string PlaybackSource
    {
        get => _prefs.GetString(KeyPlaybackSource, AndroidNowPlayingSource.AutoSource)!;
        set => Put(KeyPlaybackSource, value);
    }

    /// <summary>
    /// 자동 모드에서 영상·브라우저 앱(YouTube·크롬 등)도 음악 소스로 볼지. 기본 꺼짐 —
    /// 영상 재생 중 엉뚱한 가사를 찾아 표시하는 것을 막는다(Windows의 IncludeBrowsers에 대응).
    /// </summary>
    public bool IncludeVideoApps
    {
        get => _prefs.GetBoolean(KeyIncludeVideoApps, false);
        set
        {
            var editor = _prefs.Edit()!;
            editor.PutBoolean(KeyIncludeVideoApps, value);
            editor.Apply();
        }
    }

    /// <summary>
    /// 번역 API 사용 여부. 끄면 새 번역 요청을 보내지 않고 이미 캐시된 번역만 표시한다 —
    /// 유료 API(DeepL/Google) 사용량을 그 자리에서 끊는 스위치(Windows 트레이 토글과 같은 키·의미).
    /// 엔진·키 설정은 보존되므로 다시 켜면 원래대로 동작한다. 기본 켬.
    /// </summary>
    public bool ApiTranslationEnabled
    {
        get => _prefs.GetBoolean(KeyApiTranslationEnabled, true);
        set
        {
            var editor = _prefs.Edit()!;
            editor.PutBoolean(KeyApiTranslationEnabled, value);
            editor.Apply();
        }
    }

    /// <summary>
    /// 선택한 번역 엔진 실패 시 무키 무료 엔진(MyMemory)으로 자동 전환한다. 기본 꺼짐.
    /// 켜면 가사 텍스트가 무료 번역 공개 서버로 전송될 수 있다(Windows AppSettings와 같은 키·의미).
    /// </summary>
    public bool TranslationFallbackToFree
    {
        get => _prefs.GetBoolean(KeyTranslationFallbackToFree, false);
        set
        {
            var editor = _prefs.Edit()!;
            editor.PutBoolean(KeyTranslationFallbackToFree, value);
            editor.Apply();
        }
    }

    /// <summary>
    /// 실효 번역 엔진 판정(Windows AppSettings.EffectiveTranslationEngine과 동일 규칙):
    /// 명시 엔진이 있으면 그대로, 없으면 DeepL 키가 있으면 "deepl", 아니면 "mymemory".
    /// (Google 키만 있는 경우는 명시 선택으로만 도달하므로 판정에 넣지 않는다 — Windows와 동일.)
    /// (실제로는 사용자가 화면에서 명시 선택하므로 저장값이 곧 실효값이지만, 빈값 안전망으로 유지.)
    /// </summary>
    public string EffectiveTranslationEngine
    {
        get
        {
            var engine = _prefs.GetString(KeyTranslationEngine, null);
            if (!string.IsNullOrWhiteSpace(engine))
                return engine!.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(DeeplApiKey)
                ? TranslatorRegistry.DefaultFreeEngine
                : "deepl";
        }
    }

    private void Put(string key, string? value)
    {
        var editor = _prefs.Edit()!;
        if (string.IsNullOrWhiteSpace(value)) editor.Remove(key);
        else editor.PutString(key, value.Trim());
        editor.Apply();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
