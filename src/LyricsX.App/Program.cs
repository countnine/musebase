// LyricsX for Windows — 엔트리포인트 + 트레이 스켈레톤 (M2)
// 오버레이(M3) 전까지는 트레이 툴팁으로 현재 라인을 확인한다.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using LyricsX.App.Services;

namespace LyricsX.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var nowPlaying = await NowPlayingService.CreateAsync();
            var coordinator = new LyricsCoordinator(nowPlaying, app.Dispatcher);

            var trackItem = new MenuItem { Header = "재생 중인 곡 없음", IsEnabled = false };
            var exitItem = new MenuItem { Header = "종료" };
            exitItem.Click += (_, _) => app.Shutdown();

            var menu = new ContextMenu();
            menu.Items.Add(trackItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            var tray = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                ToolTipText = "LyricsX",
                ContextMenu = menu,
            };
            app.Exit += (_, _) => tray.Dispose();

            coordinator.StatusChanged += status =>
            {
                trackItem.Header = status;
                tray.ToolTipText = $"LyricsX\n{status}";
                Log.Write($"[status] {status}");
            };
            coordinator.CurrentLineChanged += line =>
            {
                if (line?.Content is { } content)
                {
                    var text = line.Translation is { } tr ? $"{content}\n{tr}" : content;
                    tray.ToolTipText = $"LyricsX\n{text}";
                    Log.Write($"[line] {content}{(line.Translation is { } t ? $" / {t}" : "")}");
                }
            };

            Log.Write("=== LyricsX 시작 ===");
        };
        app.Run();
    }
}

/// <summary>M2 검증용 경량 파일 로그 (%LOCALAPPDATA%\LyricsX\app.log)</summary>
internal static class Log
{
    private static readonly string LogPath = InitPath();
    private static readonly object Sync = new();

    private static string InitPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsX");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "app.log");
    }

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 로그 실패는 무시
            }
        }
    }
}
