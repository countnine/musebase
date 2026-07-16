using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyricsX.Core.Search;

namespace LyricsX.App.Services;

/// <summary>
/// 앱 설정 (%LOCALAPPDATA%\LyricsX\settings.json).
/// M4에서 DeepL 키/대상 언어를 사용한다.
/// </summary>
public sealed class AppSettings
{
    public bool OverlayVisible { get; set; } = true;
    public double? OverlayX { get; set; }
    public double? OverlayY { get; set; }

    /// <summary>오버레이 크기. 텍스트 크기는 높이에 비례해 자동 결정된다.</summary>
    public double OverlayWidth { get; set; } = 780;
    public double OverlayHeight { get; set; } = 150;

    // ---- 오버레이 스타일 (#RRGGBB) ----
    public string TextColor { get; set; } = "#FFFFFF";
    public string KaraokeColor { get; set; } = "#1DB954";
    public string TranslationColor { get; set; } = "#E8E8E8";
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 3.0;

    /// <summary>가사가 나타나고 사라질 때 부드럽게 페이드 인/아웃한다.</summary>
    public bool FadeAnimation { get; set; } = true;

    /// <summary>오버레이 배경(반투명 판)을 표시한다.</summary>
    public bool OverlayBackgroundEnabled { get; set; }

    /// <summary>오버레이 배경 색(#RRGGBB). 투명도는 OverlayBackgroundOpacity로 조절.</summary>
    public string OverlayBackgroundColor { get; set; } = "#000000";

    /// <summary>오버레이 배경 불투명도(0=완전 투명 ~ 1=불투명).</summary>
    public double OverlayBackgroundOpacity { get; set; } = 0.4;

    /// <summary>오버레이 위에 마우스를 올리면 가사·오버레이를 잠시 숨긴다(가림 방지).</summary>
    public bool HideOnMouseOver { get; set; }

    /// <summary>글자 단위 노래방(인라인 타임태그 기반). 지원 곡(Kugou/QQ 등)에서만 동작.</summary>
    public bool CharacterKaraoke { get; set; } = true;

    /// <summary>번역이 원문과 같으면 번역 줄을 숨긴다(예: 영어곡을 EN으로 번역해 원문과 중복).</summary>
    public bool HideSameTranslation { get; set; } = true;

    /// <summary>
    /// 대상 언어 번역(DeepL)만 표시하고, 제공자가 끼워 넣은 다른 언어 번역(주로 중국어)은 숨긴다.
    /// 기본 켬 → DeepL 키가 없으면 원문만 표시. 단, 대상이 중국어(ZH)면 제공자 번역이 곧 중국어이므로 그대로 표시.
    /// </summary>
    public bool ShowOnlyTargetTranslation { get; set; } = true;

    /// <summary>"틀린 가사"로 표시해 검색·표시를 막을 트랙 키 목록(정규화된 제목|아티스트).</summary>
    public List<string> SuppressedTracks { get; set; } = new();

    /// <summary>수동 싱크 오프셋(초). +면 가사가 빨라진다.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>
    /// 재생 소스 선택. "auto" = 자동 감지, 그 외 = 특정 플레이어의 SourceAppUserModelId로 고정.
    /// (macOS 버전의 플레이어 선택에 대응 — Windows에서는 SMTC 세션 단위로 고정)
    /// </summary>
    public string PlaybackSource { get; set; } = "auto";

    /// <summary>자동 모드에서 브라우저(Firefox/Chrome 등)를 음악 소스로 포함할지. 기본 제외(영상 오인식 방지).</summary>
    public bool IncludeBrowsers { get; set; }

    // ---- 가사 소스 선택 (레지스트리 기반, [[0002-pluggable-sources-and-translation]]) ----

    /// <summary>
    /// 활성 가사 소스 id 목록(LyricsSourceRegistry 기준). 기본=전부.
    /// 공개 배포 시 비공식 소스를 빼려면 LyricsSourceRegistry.OfficialIds로 좁힌다.
    /// </summary>
    public List<string> EnabledLyricsSources { get; set; } = LyricsSourceRegistry.AllIds.ToList();

    // ---- 번역 엔진 선택 ----

    /// <summary>번역 엔진 id(TranslatorRegistry). 비면 EffectiveTranslationEngine으로 자동 결정.</summary>
    public string? TranslationEngine { get; set; }

    /// <summary>LibreTranslate 엔드포인트(자체호스팅 등). 비면 레지스트리 기본값 사용.</summary>
    public string? LibreTranslateEndpoint { get; set; }

    /// <summary>
    /// 실효 번역 엔진. 명시값이 있으면 그대로, 없으면 DeepL 키가 있으면 deepl(기존 사용자 보존),
    /// 없으면 libretranslate(무키 무료로 설치 후 바로 동작).
    /// </summary>
    [JsonIgnore]
    public string EffectiveTranslationEngine =>
        !string.IsNullOrWhiteSpace(TranslationEngine)
            ? TranslationEngine!.Trim().ToLowerInvariant()
            : (string.IsNullOrWhiteSpace(DeeplApiKey) ? "libretranslate" : "deepl");

    /// <summary>DeepL API 키(평문) — 앱 내에서만 사용, 파일엔 저장하지 않는다(암호화본만 저장).</summary>
    [JsonIgnore]
    public string? DeeplApiKey { get; set; }

    /// <summary>DeepL 키의 DPAPI 암호문(base64). settings.json에 실제 저장되는 값.</summary>
    [JsonPropertyName("deeplApiKeyEnc")]
    public string? DeeplApiKeyEncrypted { get; set; }

    /// <summary>구버전 평문 키("deeplApiKey") — 마이그레이션 전용(읽기만, 저장 시 제거).</summary>
    [JsonPropertyName("deeplApiKey")]
    public string? LegacyDeeplApiKey { get; set; }

    /// <summary>DeepL target_lang. 최초 실행 시 시스템 언어로 기본 설정(미지원이면 EN-US).</summary>
    public string TargetLanguage { get; set; } = DefaultTargetLanguage();

    /// <summary>UI 표시 언어. "system"이면 시스템 언어를 따르고, 미지원이면 영어로 폴백.</summary>
    public string UiLanguage { get; set; } = "system";

    [JsonIgnore]
    public string EffectiveTargetLanguage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? DefaultTargetLanguage() : TargetLanguage.Trim().ToUpperInvariant();

    /// <summary>
    /// 시스템 UI 언어를 DeepL target_lang 코드로 매핑한 기본 번역 대상 언어.
    /// (설정 로드 시점에는 아직 UI 언어 오버라이드 전이라 CurrentUICulture = 시스템 언어)
    /// </summary>
    public static string DefaultTargetLanguage()
    {
        var c = CultureInfo.CurrentUICulture;
        var two = c.TwoLetterISOLanguageName.ToUpperInvariant();
        return two switch
        {
            "EN" => "EN-US",
            "PT" => c.Name.EndsWith("-PT", StringComparison.OrdinalIgnoreCase) ? "PT-PT" : "PT-BR",
            "ZH" => c.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ? "ZH-HANT" : "ZH",
            // DeepL이 target_lang으로 지원하는 언어면 2글자 코드 그대로 사용
            "KO" or "JA" or "DE" or "FR" or "ES" or "IT" or "NL" or "PL" or "RU" or "UK" or "TR"
                or "CS" or "ID" or "AR" or "VI" or "BG" or "DA" or "EL" or "ET" or "FI" or "HU"
                or "LT" or "LV" or "NB" or "RO" or "SK" or "SL" or "SV" => two,
            _ => "EN-US", // 미지원 시스템 언어 → 영어
        };
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsX", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // null 필드(구 평문 키 등) 미기록
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                settings.ResolveSecrets();
                return settings;
            }
        }
        catch
        {
            // 손상된 설정은 기본값으로
        }
        return new AppSettings();
    }

    /// <summary>암호문 복호화 또는 구버전 평문 키 마이그레이션 → 평문 DeeplApiKey 확정.</summary>
    private void ResolveSecrets()
    {
        if (!string.IsNullOrEmpty(DeeplApiKeyEncrypted))
            DeeplApiKey = Secret.Unprotect(DeeplApiKeyEncrypted);
        else if (!string.IsNullOrWhiteSpace(LegacyDeeplApiKey))
            DeeplApiKey = LegacyDeeplApiKey; // 구버전 평문 → 다음 Save에서 암호화
        LegacyDeeplApiKey = null;            // 평문 필드는 더 이상 보관/기록하지 않음
    }

    public void Save()
    {
        try
        {
            // 평문 키는 파일에 쓰지 않고, DPAPI 암호문만 저장
            DeeplApiKeyEncrypted = Secret.Protect(DeeplApiKey);
            LegacyDeeplApiKey = null;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 저장 실패는 치명적이지 않음
        }
    }
}
