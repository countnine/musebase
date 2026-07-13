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
    public double FontSize { get; set; } = 40;
    public double TranslationFontSize { get; set; } = 26;

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
