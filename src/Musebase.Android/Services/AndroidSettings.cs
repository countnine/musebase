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
    private const string KeyTargetLanguage = "TargetLanguage";

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

    /// <summary>번역 대상 언어(DeepL target_lang 코드). 비면 기기 로케일 기본값을 쓴다.</summary>
    public string? TargetLanguage
    {
        get => NullIfBlank(_prefs.GetString(KeyTargetLanguage, null));
        set => Put(KeyTargetLanguage, value);
    }

    /// <summary>
    /// 실효 번역 엔진 판정(Windows AppSettings.EffectiveTranslationEngine과 동일 규칙):
    /// 명시 엔진이 있으면 그대로, 없으면 DeepL 키가 있으면 "deepl", 아니면 "mymemory".
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
