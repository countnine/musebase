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
    private const string KeyPreferredSources = "PreferredSources";
    private const string KeyOverlayBubbleMode = "OverlayBubbleMode";
    private const string KeyOverlayPeekOnNewLine = "OverlayPeekOnNewLine";
    private const string KeyOverlayRatioX = "OverlayRatioX";
    private const string KeyOverlayRatioY = "OverlayRatioY";
    private const string KeyBubbleRatioX = "BubbleRatioX";
    private const string KeyBubbleRatioY = "BubbleRatioY";

    // 오버레이 스타일(Windows AppSettings와 같은 키·의미 — 색은 "#RRGGBB" 문자열).
    private const string KeyOverlayTextColor = "TextColor";
    private const string KeyOverlayKaraokeColor = "KaraokeColor";
    private const string KeyOverlayTranslationColor = "TranslationColor";
    private const string KeyOverlayBackgroundEnabled = "OverlayBackgroundEnabled";
    private const string KeyOverlayBackgroundColor = "OverlayBackgroundColor";
    private const string KeyOverlayBackgroundOpacity = "OverlayBackgroundOpacity";
    private const string KeyOverlayCornerRadius = "OverlayCornerRadius";
    private const string KeyOverlayFontSizeSp = "OverlayFontSizeSp";
    private const string KeyOverlayFadeAnimation = "FadeAnimation";
    private const string KeyCharacterKaraoke = "CharacterKaraoke";
    private const string KeyShowOnlyTargetTranslation = "ShowOnlyTargetTranslation";

    /// <summary>위치 비율 미설정 표식(기본 위치를 쓰라는 뜻).</summary>
    public const float UnsetRatio = -1f;

    // ---- 오버레이 스타일 ----

    /// <summary>가사 원문 색("#RRGGBB"). Windows 기본과 동일.</summary>
    public string OverlayTextColor
    {
        get => _prefs.GetString(KeyOverlayTextColor, "#FFFFFF")!;
        set => Put(KeyOverlayTextColor, value);
    }

    /// <summary>카라오케 채움 색.</summary>
    public string OverlayKaraokeColor
    {
        get => _prefs.GetString(KeyOverlayKaraokeColor, "#FFEB3B")!;
        set => Put(KeyOverlayKaraokeColor, value);
    }

    /// <summary>번역 줄 색.</summary>
    public string OverlayTranslationColor
    {
        get => _prefs.GetString(KeyOverlayTranslationColor, "#E8E8E8")!;
        set => Put(KeyOverlayTranslationColor, value);
    }

    /// <summary>가사 뒤 반투명 배경판 표시 여부.</summary>
    public bool OverlayBackgroundEnabled
    {
        get => _prefs.GetBoolean(KeyOverlayBackgroundEnabled, true);
        set => PutBool(KeyOverlayBackgroundEnabled, value);
    }

    /// <summary>배경판 색.</summary>
    public string OverlayBackgroundColor
    {
        get => _prefs.GetString(KeyOverlayBackgroundColor, "#000000")!;
        set => Put(KeyOverlayBackgroundColor, value);
    }

    /// <summary>배경판 불투명도(0~1).</summary>
    public float OverlayBackgroundOpacity
    {
        get => _prefs.GetFloat(KeyOverlayBackgroundOpacity, 0.7f);
        set => PutFloat(KeyOverlayBackgroundOpacity, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>배경판 모서리 둥글기(dp).</summary>
    public int OverlayCornerRadius
    {
        get => _prefs.GetInt(KeyOverlayCornerRadius, 18);
        set => PutInt(KeyOverlayCornerRadius, Math.Clamp(value, 0, 40));
    }

    /// <summary>가사 원문 글자 크기(sp). 번역 줄은 이 값에 비례한다.</summary>
    public int OverlayFontSizeSp
    {
        get => _prefs.GetInt(KeyOverlayFontSizeSp, 22);
        set => PutInt(KeyOverlayFontSizeSp, Math.Clamp(value, 12, 40));
    }

    /// <summary>오버레이가 나타나고 사라질 때 페이드 효과를 쓸지.</summary>
    public bool OverlayFadeAnimation
    {
        get => _prefs.GetBoolean(KeyOverlayFadeAnimation, true);
        set => PutBool(KeyOverlayFadeAnimation, value);
    }

    /// <summary>글자 단위 카라오케(끄면 줄 단위 채움).</summary>
    public bool CharacterKaraoke
    {
        get => _prefs.GetBoolean(KeyCharacterKaraoke, true);
        set => PutBool(KeyCharacterKaraoke, value);
    }

    /// <summary>대상 언어로 번역된 줄만 표시(끄면 제공자 번역도 함께 — 중국어 등이 뜰 수 있다).</summary>
    public bool ShowOnlyTargetTranslation
    {
        get => _prefs.GetBoolean(KeyShowOnlyTargetTranslation, true);
        set => PutBool(KeyShowOnlyTargetTranslation, value);
    }

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
    /// 선호 음악 앱(패키지) 목록. 비어 있으면(기본) 자동 — 종전대로 영상 앱 제외 규칙만 쓴다.
    /// 하나 이상 고르면 **그 앱들만** 가사 소스로 인정해, 팟캐스트·영상 앱이 잡히지 않는다.
    /// 쉼표로 이어 저장한다(직렬화 키·값은 영어 패키지명 유지).
    /// </summary>
    public IReadOnlyList<string> PreferredSources
    {
        get
        {
            var raw = _prefs.GetString(KeyPreferredSources, null);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        set => Put(KeyPreferredSources, value is null ? null : string.Join(",", value));
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
    /// 버블(플로팅) 모드. 켜면 가사 밴드 대신 작은 원형 버블이 떠 있고, 버블을 탭할 때만
    /// 가사 밴드를 펼친다 — 화면 하단이 가려지거나 밴드가 방해될 때 쓰는 표시 방식. 기본 꺼짐.
    /// </summary>
    public bool OverlayBubbleMode
    {
        get => _prefs.GetBoolean(KeyOverlayBubbleMode, false);
        set => PutBool(KeyOverlayBubbleMode, value);
    }

    /// <summary>
    /// 버블 모드에서 접혀 있을 때 새 가사 줄이 나오면 잠깐(약 3초) 자동으로 펼쳤다 접는다. 기본 켬.
    /// (끄면 버블을 직접 탭할 때만 가사가 보인다.)
    /// </summary>
    public bool OverlayPeekOnNewLine
    {
        get => _prefs.GetBoolean(KeyOverlayPeekOnNewLine, true);
        set => PutBool(KeyOverlayPeekOnNewLine, value);
    }

    /// <summary>
    /// 가사 밴드 위치(화면 여유 공간 대비 0~1 비율, <see cref="UnsetRatio"/>면 기본 위치=하단 중앙).
    /// 픽셀이 아닌 비율로 저장해 화면 회전·해상도 변화에도 화면 밖으로 나가지 않는다.
    /// </summary>
    public (float X, float Y) OverlayRatio
    {
        get => (_prefs.GetFloat(KeyOverlayRatioX, UnsetRatio), _prefs.GetFloat(KeyOverlayRatioY, UnsetRatio));
        set => PutRatio(KeyOverlayRatioX, KeyOverlayRatioY, value);
    }

    /// <summary>버블 위치(같은 규칙. 미설정이면 오른쪽 아래).</summary>
    public (float X, float Y) BubbleRatio
    {
        get => (_prefs.GetFloat(KeyBubbleRatioX, UnsetRatio), _prefs.GetFloat(KeyBubbleRatioY, UnsetRatio));
        set => PutRatio(KeyBubbleRatioX, KeyBubbleRatioY, value);
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

    private void PutBool(string key, bool value)
    {
        var editor = _prefs.Edit()!;
        editor.PutBoolean(key, value);
        editor.Apply();
    }

    private void PutInt(string key, int value)
    {
        var editor = _prefs.Edit()!;
        editor.PutInt(key, value);
        editor.Apply();
    }

    private void PutFloat(string key, float value)
    {
        var editor = _prefs.Edit()!;
        editor.PutFloat(key, value);
        editor.Apply();
    }

    /// <summary>"#RRGGBB" 문자열 → 안드로이드 Color. 형식이 틀리면 기본값을 쓴다.</summary>
    public static global::Android.Graphics.Color ParseColor(string? hex, global::Android.Graphics.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return global::Android.Graphics.Color.ParseColor(hex!.Trim()); }
        catch { return fallback; }
    }

    private void PutRatio(string keyX, string keyY, (float X, float Y) value)
    {
        var editor = _prefs.Edit()!;
        editor.PutFloat(keyX, value.X);
        editor.PutFloat(keyY, value.Y);
        editor.Apply();
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
