using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>"틀린 가사"로 표시해 검색·표시를 막을 트랙 키 목록(정규화된 제목|아티스트).</summary>
    public List<string> SuppressedTracks { get; set; } = new();

    /// <summary>수동 싱크 오프셋(초). +면 가사가 빨라진다.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>DeepL API 키 (미설정 시 기계번역 폴백 없음)</summary>
    public string? DeeplApiKey { get; set; }

    /// <summary>DeepL target_lang. 미설정/공백이면 한국어(KO).</summary>
    public string TargetLanguage { get; set; } = "KO";

    [JsonIgnore]
    public string EffectiveTargetLanguage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? "KO" : TargetLanguage.Trim().ToUpperInvariant();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsX", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // 손상된 설정은 기본값으로
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 저장 실패는 치명적이지 않음
        }
    }
}
