using Microsoft.Win32;

namespace Musebase.Windows.Services;

/// <summary>
/// Windows 시작 시 자동 실행 관리 (HKCU Run 키 — 관리자 권한 불필요).
/// 원본 macOS의 LaunchAtLogin/LoginItems 헬퍼에 해당하는 Windows 구현.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Musebase";
    private const string LegacyValueName = "LyricsX"; // 개명 전 항목 — 설정 변경 시 정리

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
