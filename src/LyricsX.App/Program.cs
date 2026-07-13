// LyricsX for Windows — 엔트리포인트 (M3: 오버레이 + 트레이 제어)

using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using LyricsX.App.Overlay;
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
            var settings = AppSettings.Load();
            var nowPlaying = await NowPlayingService.CreateAsync();

            // 번역: SQLite 라인 캐시 + DeepL(키 있을 때만)
            var cacheDb = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LyricsX", "translations.db");
            var translationCache = new LyricsX.Core.Translation.SqliteTranslationCache(cacheDb);
            LyricsX.Core.Translation.LyricsTranslationService BuildTranslation() => new(
                string.IsNullOrWhiteSpace(settings.DeeplApiKey)
                    ? null
                    : new LyricsX.Core.Translation.DeeplTranslator(settings.DeeplApiKey),
                translationCache);

            var coordinator = new LyricsCoordinator(nowPlaying, app.Dispatcher)
            {
                ManualOffsetSeconds = settings.ManualOffsetSeconds,
                Translation = BuildTranslation(),
                TargetLanguage = settings.EffectiveTargetLanguage,
            };

            var overlay = new OverlayWindow(settings);
            if (settings.OverlayVisible) overlay.Show();

            // ---- 트레이 메뉴 ----
            var trackItem = new MenuItem { Header = "재생 중인 곡 없음", IsEnabled = false };
            var overlayToggle = new MenuItem { Header = "오버레이 표시", IsCheckable = true, IsChecked = settings.OverlayVisible };
            var moveToggle = new MenuItem { Header = "오버레이 위치 이동 모드", IsCheckable = true };
            var offsetLabel = new MenuItem { IsEnabled = false };
            var offsetPlus = new MenuItem { Header = "가사 빠르게 (+0.5초)" };
            var offsetMinus = new MenuItem { Header = "가사 느리게 (-0.5초)" };
            var offsetReset = new MenuItem { Header = "오프셋 초기화" };
            var settingsItem = new MenuItem { Header = "설정…" };
            var exitItem = new MenuItem { Header = "종료" };

            SettingsWindow? settingsWindow = null;
            settingsItem.Click += (_, _) =>
            {
                if (settingsWindow is { IsLoaded: true })
                {
                    settingsWindow.Activate();
                    return;
                }
                settingsWindow = new SettingsWindow(settings, onSaved: () =>
                {
                    coordinator.Translation = BuildTranslation();
                    coordinator.TargetLanguage = settings.EffectiveTargetLanguage;
                    overlay.ApplyFontSizes();
                    Log.Write($"[settings] 저장됨: lang={settings.EffectiveTargetLanguage}, key={(settings.DeeplApiKey is null ? "없음" : "설정됨")}");
                });
                settingsWindow.Show();
            };

            void UpdateOffsetLabel() =>
                offsetLabel.Header = $"싱크 오프셋: {coordinator.ManualOffsetSeconds:+0.0;-0.0;0}초";
            UpdateOffsetLabel();

            overlayToggle.Click += (_, _) =>
            {
                settings.OverlayVisible = overlayToggle.IsChecked;
                if (overlayToggle.IsChecked) overlay.Show();
                else overlay.Hide();
                settings.Save();
            };
            moveToggle.Click += (_, _) => overlay.SetMoveMode(moveToggle.IsChecked);
            offsetPlus.Click += (_, _) => AdjustOffset(0.5);
            offsetMinus.Click += (_, _) => AdjustOffset(-0.5);
            offsetReset.Click += (_, _) => AdjustOffset(null);
            exitItem.Click += (_, _) => app.Shutdown();

            void AdjustOffset(double? delta)
            {
                coordinator.ManualOffsetSeconds = delta is { } d ? coordinator.ManualOffsetSeconds + d : 0;
                settings.ManualOffsetSeconds = coordinator.ManualOffsetSeconds;
                settings.Save();
                UpdateOffsetLabel();
            }

            var menu = new ContextMenu();
            menu.Items.Add(trackItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(overlayToggle);
            menu.Items.Add(moveToggle);
            menu.Items.Add(new Separator());
            menu.Items.Add(offsetLabel);
            menu.Items.Add(offsetPlus);
            menu.Items.Add(offsetMinus);
            menu.Items.Add(offsetReset);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(exitItem);

            var tray = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                ToolTipText = "LyricsX",
                ContextMenu = menu,
            };
            app.Exit += (_, _) =>
            {
                tray.Dispose();
                settings.Save();
            };

            // ---- 이벤트 배선 ----
            coordinator.StatusChanged += status =>
            {
                trackItem.Header = status;
                tray.ToolTipText = $"LyricsX\n{status}";
                Log.Write($"[status] {status}");
            };
            coordinator.CurrentLineChanged += line =>
            {
                overlay.SetLine(line);
                if (line?.Content is { } content)
                    Log.Write($"[line] {content}{(line.Translation is { } t ? $" / {t}" : "")}");
            };
            coordinator.LineProgressChanged += overlay.SetProgress;

            // --demo: 내장 데모 가사를 3초 주기로 순환 (오버레이 검증/시연용, SMTC 불필요)
            if (args.Contains("--demo"))
            {
                overlay.Show();
                var demoLines = new (string Content, string Translation)[]
                {
                    ("沈むように溶けてゆくように", "가라앉듯이 녹아내리듯이"),
                    ("二人だけの空が広がる夜に", "둘만의 하늘이 펼쳐지는 밤에"),
                    ("「さよなら」だけだった", "「안녕」뿐이었어"),
                };
                var demoIndex = 0;
                var started = DateTime.Now;
                var demoTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50),
                };
                demoTimer.Tick += (_, _) =>
                {
                    var elapsed = (DateTime.Now - started).TotalSeconds;
                    var slot = (int)(elapsed / 3.0) % demoLines.Length;
                    if (slot != demoIndex || elapsed < 0.1)
                    {
                        demoIndex = slot;
                        var (c, t) = demoLines[slot];
                        overlay.SetLine(new DisplayLine(c, t));
                    }
                    overlay.SetProgress(elapsed % 3.0 / 3.0);
                };
                demoTimer.Start();
                Log.Write("[demo] 데모 모드 시작");
            }

            Log.Write("=== LyricsX 시작 (M4) ===");
        };
        app.Run();
    }
}

/// <summary>경량 파일 로그 (%LOCALAPPDATA%\LyricsX\app.log)</summary>
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
