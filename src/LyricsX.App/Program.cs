// LyricsX for Windows — 엔트리포인트 (M3: 오버레이 + 트레이 제어)

using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using LyricsX.App.Overlay;
using LyricsX.App.Services;
using LyricsX.Engine;
using Velopack;

namespace LyricsX.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack: 설치/업데이트/제거 훅 처리. 반드시 다른 앱 로직보다 먼저 실행해야 한다.
        VelopackApp.Build().Run();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var settings = AppSettings.Load();
            Loc.Initialize(settings.UiLanguage); // UI 다국어: 창 생성 전에 언어 확정
            var nowPlaying = await NowPlayingService.CreateAsync();

            // 번역: SQLite 라인 캐시 + 레지스트리에서 선택된 엔진(키 없으면 무키 무료로 폴백)
            var cacheDb = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LyricsX", "translations.db");
            var translationCache = new LyricsX.Core.Translation.SqliteTranslationCache(cacheDb);
            LyricsX.Core.Translation.LyricsTranslationService BuildTranslation()
            {
                var options = new LyricsX.Core.Translation.TranslatorOptions(
                    DeeplApiKey: settings.DeeplApiKey,
                    LibreEndpoint: settings.LibreTranslateEndpoint);
                var translator = LyricsX.Core.Translation.TranslatorRegistry.Build(
                    settings.EffectiveTranslationEngine, options);
                Log.Write($"[translate] 엔진={settings.EffectiveTranslationEngine}, 활성={(translator is not null)}");
                return new LyricsX.Core.Translation.LyricsTranslationService(translator, translationCache);
            }

            // 가사 소스: 레지스트리에서 활성 소스만 조합해 검색 서비스 구성
            var search = new LyricsX.Core.Search.LyricsSearchService(
                LyricsX.Core.Search.LyricsSourceRegistry.Build(settings.EnabledLyricsSources));
            Log.Write($"[sources] 활성 가사 소스: {string.Join(", ", settings.EnabledLyricsSources)}");

            var coordinator = new LyricsCoordinator(nowPlaying, new WpfEngineDispatcher(app.Dispatcher), search)
            {
                ManualOffsetSeconds = settings.ManualOffsetSeconds,
                Translation = BuildTranslation(),
                TargetLanguage = settings.EffectiveTargetLanguage,
                ShowOnlyTargetTranslation = settings.ShowOnlyTargetTranslation,
                Cache = new LyricsX.Core.Search.LyricsCacheStore(cacheDb),
                Log = Log.Write,
            };

            // "틀린 가사" 억제 목록을 설정에서 복원하고 변경 시 영속화
            foreach (var key in settings.SuppressedTracks) coordinator.SuppressedTrackKeys.Add(key);
            coordinator.SuppressedTracksChanged += () =>
            {
                settings.SuppressedTracks = coordinator.SuppressedTrackKeys.ToList();
                settings.Save();
            };

            var overlay = new OverlayWindow(settings);
            overlay.SetUserVisible(settings.OverlayVisible);

            // 전체화면 앱(게임/영상) 활성 시 오버레이 자동 숨김
            var fullscreenDetector = new FullscreenDetector(app.Dispatcher);
            fullscreenDetector.FullscreenChanged += full =>
            {
                overlay.SetFullscreenSuppressed(full);
                Log.Write($"[fullscreen] {(full ? "감지 → 오버레이 숨김" : "해제 → 오버레이 복원")}");
            };

            // 재생 소스 선택 적용 (자동/특정 플레이어, 브라우저 제외 기본)
            nowPlaying.SetSource(settings.PlaybackSource, settings.IncludeBrowsers);

            // 일시정지 중 오버레이 자동 숨김 (--demo에서는 재생 상태가 없으므로 제외)
            if (!args.Contains("--demo"))
            {
                overlay.SetPausedSuppressed(!nowPlaying.IsPlaying);
                nowPlaying.IsPlayingChanged += playing =>
                    app.Dispatcher.BeginInvoke(() => overlay.SetPausedSuppressed(!playing));

                // 오버레이 좌측 재생 컨트롤(이전/재생·정지/다음). 마우스 오버 시에만 표시.
                overlay.EnableMediaControls(
                    controlsProvider: () => nowPlaying.GetControls(),
                    playingProvider: () => nowPlaying.IsPlaying,
                    onPrevious: () => _ = nowPlaying.SkipPreviousAsync(),
                    onPlayPause: () => _ = nowPlaying.TogglePlayPauseAsync(),
                    onNext: () => _ = nowPlaying.SkipNextAsync());
            }

            // ---- 트레이 메뉴 ----
            var trackItem = new MenuItem { Header = Loc.T("status.noTrack"), IsEnabled = false };
            var overlayToggle = new MenuItem { Header = Loc.T("tray.overlay.show"), IsCheckable = true, IsChecked = settings.OverlayVisible };
            var moveToggle = new MenuItem { Header = Loc.T("tray.overlay.moveMode"), IsCheckable = true };
            var sourceMenu = new MenuItem { Header = Loc.T("tray.source") };

            void ApplySource(string mode, bool includeBrowsers)
            {
                settings.PlaybackSource = mode;
                settings.IncludeBrowsers = includeBrowsers;
                settings.Save();
                nowPlaying.SetSource(mode, includeBrowsers);
                Log.Write($"[source] 소스={mode}, 브라우저포함={includeBrowsers}");
            }

            // 트레이 열릴 때마다 현재 감지된 SMTC 세션으로 소스 하위 메뉴를 재구성한다.
            void RebuildSourceMenu()
            {
                sourceMenu.Items.Clear();
                var mode = nowPlaying.SourceMode;

                var autoItem = new MenuItem
                {
                    Header = Loc.T("tray.source.auto"),
                    IsCheckable = true,
                    IsChecked = string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase),
                };
                autoItem.Click += (_, _) => ApplySource("auto", settings.IncludeBrowsers);
                sourceMenu.Items.Add(autoItem);

                var browsersItem = new MenuItem
                {
                    Header = Loc.T("tray.source.includeBrowsers"),
                    IsCheckable = true,
                    IsChecked = settings.IncludeBrowsers,
                };
                browsersItem.Click += (_, _) => ApplySource(nowPlaying.SourceMode, browsersItem.IsChecked);
                sourceMenu.Items.Add(browsersItem);

                sourceMenu.Items.Add(new Separator());

                var sources = nowPlaying.GetAvailableSources();
                if (sources.Count == 0)
                {
                    sourceMenu.Items.Add(new MenuItem { Header = Loc.T("tray.source.none"), IsEnabled = false });
                }
                else
                {
                    foreach (var id in sources)
                    {
                        var captured = id;
                        var item = new MenuItem
                        {
                            Header = NowPlayingService.IsBrowser(id) ? $"{id}  🌐" : id,
                            IsCheckable = true,
                            IsChecked = string.Equals(mode, id, StringComparison.OrdinalIgnoreCase),
                        };
                        item.Click += (_, _) => ApplySource(captured, settings.IncludeBrowsers);
                        sourceMenu.Items.Add(item);
                    }
                }
            }
            var offsetLabel = new MenuItem { IsEnabled = false };
            var offsetPlus = new MenuItem { Header = Loc.T("tray.offset.faster") };
            var offsetMinus = new MenuItem { Header = Loc.T("tray.offset.slower") };
            var offsetReset = new MenuItem { Header = Loc.T("tray.offset.reset") };
            var startupToggle = new MenuItem
            {
                Header = Loc.T("tray.startup"),
                IsCheckable = true,
                IsChecked = StartupManager.IsEnabled(),
            };
            startupToggle.Click += (_, _) =>
            {
                try
                {
                    StartupManager.SetEnabled(startupToggle.IsChecked);
                }
                catch (Exception e)
                {
                    Log.Write($"[startup] 설정 실패: {e.Message}");
                    startupToggle.IsChecked = StartupManager.IsEnabled();
                }
            };
            var searchItem = new MenuItem { Header = Loc.T("tray.search") };
            searchItem.Click += (_, _) => new SearchWindow(coordinator).Show();

            // ---- 현재 가사 편집 / 내보내기 ----
            var editItem = new MenuItem { Header = Loc.T("tray.edit") };
            var exportItem = new MenuItem { Header = Loc.T("tray.export") };
            LyricsEditorWindow? editorWindow = null;

            editItem.Click += (_, _) =>
            {
                if (coordinator.CurrentLyrics is not { } lyrics || coordinator.CurrentTrack is not { } track) return;
                if (editorWindow is { IsLoaded: true })
                {
                    editorWindow.Activate();
                    return;
                }
                editorWindow = new LyricsEditorWindow(
                    track.ToString(), lyrics,
                    edited => coordinator.SaveEditedLyrics(track, edited));
                editorWindow.Show();
            };

            exportItem.Click += (_, _) =>
            {
                if (coordinator.CurrentLyrics is not { } lyrics || coordinator.CurrentTrack is not { } track) return;
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = Loc.T("export.filter"),
                    DefaultExt = ".lrc",
                    FileName = SanitizeFileName($"{track.Artist} - {track.Title}") + ".lrc",
                };
                if (dialog.ShowDialog() != true) return;
                try
                {
                    // 이중언어: [mm:ss.xx]원문【번역】 (표준 플레이어 호환).
                    // 화면과 동일하게 대상 언어(기계번역 tr:{target}) 번역을 우선 포함.
                    var lrc = lyrics.ToLegacyString(coordinator.TargetLanguage?.ToLowerInvariant());
                    File.WriteAllText(dialog.FileName, lrc, new System.Text.UTF8Encoding(false));
                    Log.Write($"[export] {dialog.FileName}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("export.fail", ("error", ex.Message)), "LyricsX", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            // 현재 가사가 틀렸을 때: 표시 중단 + 캐시 제거 + 재검색 억제
            var wrongItem = new MenuItem { Header = Loc.T("tray.wrong") };
            wrongItem.Click += (_, _) => coordinator.MarkWrongLyrics();

            var settingsItem = new MenuItem { Header = Loc.T("tray.settings") };
            var exitItem = new MenuItem { Header = Loc.T("tray.exit") };

            // ---- 자동 업데이트 (Velopack + GitHub Releases) ----
            var updater = new UpdateService();
            Velopack.UpdateInfo? pendingUpdate = null;
            var updateItem = new MenuItem { Header = Loc.T("tray.update.check", ("version", updater.CurrentVersion.ToString())) };

            void UpdateUpdateItemText() =>
                updateItem.Header = pendingUpdate is { } p
                    ? Loc.T("tray.update.install", ("version", p.TargetFullRelease.Version.ToString()))
                    : Loc.T("tray.update.check", ("version", updater.CurrentVersion.ToString()));

            async Task RunUpdateCheckAsync(bool userInitiated)
            {
                try
                {
                    if (!updater.IsInstalled)
                    {
                        if (userInitiated)
                            MessageBox.Show(
                                Loc.T("update.dev", ("version", updater.CurrentVersion.ToString())),
                                Loc.T("update.title"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var info = pendingUpdate ?? await updater.CheckAsync();
                    if (info is null)
                    {
                        if (userInitiated)
                            MessageBox.Show(
                                Loc.T("update.latest", ("version", updater.CurrentVersion.ToString())),
                                Loc.T("update.title"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var newVersion = info.TargetFullRelease.Version.ToString();
                    pendingUpdate = info;
                    UpdateUpdateItemText();

                    if (!userInitiated)
                    {
                        Log.Write($"[update] 새 버전 사용 가능: v{newVersion}");
                        return; // 자동 확인은 메뉴 라벨만 갱신(비침습)
                    }

                    var answer = MessageBox.Show(
                        Loc.T("update.available", ("version", newVersion), ("current", updater.CurrentVersion.ToString())),
                        Loc.T("update.title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes)
                    {
                        settings.Save();
                        Log.Write($"[update] v{newVersion} 설치 후 재시작");
                        await updater.DownloadAndApplyAsync(info); // 프로세스 재시작
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"[update] 확인 실패: {e.Message}");
                    if (userInitiated)
                        MessageBox.Show(
                            Loc.T("update.error", ("error", e.Message)),
                            Loc.T("update.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            updateItem.Click += async (_, _) => await RunUpdateCheckAsync(userInitiated: true);

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
                    coordinator.ShowOnlyTargetTranslation = settings.ShowOnlyTargetTranslation;
                    coordinator.RefreshCurrentLine(); // 표시 정책 변경 즉시 반영
                    overlay.ApplyStyle();
                    Log.Write($"[settings] 저장됨: lang={settings.EffectiveTargetLanguage}, key={(settings.DeeplApiKey is null ? "없음" : "설정됨")}");
                });
                settingsWindow.Show();
            };

            void UpdateOffsetLabel() =>
                offsetLabel.Header = Loc.T("tray.offset.label", ("value", coordinator.ManualOffsetSeconds.ToString("+0.0;-0.0;0")));
            UpdateOffsetLabel();

            overlayToggle.Click += (_, _) =>
            {
                settings.OverlayVisible = overlayToggle.IsChecked;
                overlay.SetUserVisible(overlayToggle.IsChecked);
                settings.Save();
            };
            moveToggle.Click += (_, _) => overlay.SetMoveMode(moveToggle.IsChecked);
            overlay.MoveModeChanged += moveMode => moveToggle.IsChecked = moveMode; // 자물쇠 버튼과 동기화
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
            // 편집/내보내기는 재생 곡+가사가 있을 때만 활성
            menu.Opened += (_, _) =>
            {
                var hasLyrics = coordinator.CurrentLyrics is not null && coordinator.CurrentTrack is not null;
                editItem.IsEnabled = hasLyrics;
                exportItem.IsEnabled = hasLyrics;
                wrongItem.IsEnabled = hasLyrics;
                RebuildSourceMenu();
            };
            menu.Items.Add(trackItem);
            menu.Items.Add(searchItem);
            menu.Items.Add(editItem);
            menu.Items.Add(exportItem);
            menu.Items.Add(wrongItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(overlayToggle);
            menu.Items.Add(moveToggle);
            menu.Items.Add(sourceMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(offsetLabel);
            menu.Items.Add(offsetPlus);
            menu.Items.Add(offsetMinus);
            menu.Items.Add(offsetReset);
            menu.Items.Add(new Separator());
            menu.Items.Add(startupToggle);
            menu.Items.Add(updateItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(exitItem);

            var tray = new TaskbarIcon
            {
                Icon = CreateTrayIcon(),
                ToolTipText = Loc.T("tray.tooltip.version", ("version", updater.CurrentVersion.ToString())),
                ContextMenu = menu,
            };
            // H.NotifyIcon 2.x는 명시적 생성이 필요할 수 있다 (없으면 아이콘 미표시)
            try
            {
                tray.ForceCreate(enablesEfficiencyMode: false);
            }
            catch (Exception e)
            {
                Log.Write($"[tray] ForceCreate 실패: {e.Message}");
            }
            app.Exit += (_, _) =>
            {
                tray.Dispose();
                settings.Save();
            };

            // ---- 이벤트 배선 ----
            // 엔진은 구조화된 LyricsStatus를 발행 → 여기서 현지화(UI 분리)
            static string LocalizeStatus(LyricsStatus s) => s.Kind switch
            {
                LyricsStatusKind.NoTrack => Loc.T("status.noTrack"),
                LyricsStatusKind.HiddenByUser => Loc.T("status.hidden.user", ("track", s.Track ?? "")),
                LyricsStatusKind.Cache => Loc.T("status.cache", ("track", s.Track ?? ""), ("service", s.Service ?? "")),
                LyricsStatusKind.Searching => Loc.T("status.searching", ("track", s.Track ?? "")),
                LyricsStatusKind.Found => Loc.T("status.found", ("track", s.Track ?? ""), ("service", s.Service ?? ""), ("quality", (s.Quality ?? 0).ToString("0.00"))),
                LyricsStatusKind.NotFound => Loc.T("status.notFound", ("track", s.Track ?? "")),
                LyricsStatusKind.Wrong => Loc.T("status.wrong", ("track", s.Track ?? "")),
                LyricsStatusKind.Manual => Loc.T("status.manual", ("track", s.Track ?? ""), ("service", s.Service ?? "")),
                LyricsStatusKind.Edited => Loc.T("status.edited", ("track", s.Track ?? "")),
                _ => "",
            };

            coordinator.StatusChanged += status =>
            {
                var text = LocalizeStatus(status);
                trackItem.Header = text;
                tray.ToolTipText = Loc.T("tray.tooltip.status", ("status", text));
                Log.Write($"[status] {text}");
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
                overlay.SetUserVisible(true);
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
                        overlay.SetLine(new DisplayLine(c, t, null, 3.0));
                    }
                    overlay.SetProgress(elapsed % 3.0); // 라인 시작 이후 경과(초)
                };
                demoTimer.Start();
                Log.Write("[demo] 데모 모드 시작");
            }

            // 언어 변경 시 트레이 메뉴·툴팁을 즉시 다시 현지화
            void ApplyMenuText()
            {
                overlayToggle.Header = Loc.T("tray.overlay.show");
                moveToggle.Header = Loc.T("tray.overlay.moveMode");
                sourceMenu.Header = Loc.T("tray.source");
                offsetPlus.Header = Loc.T("tray.offset.faster");
                offsetMinus.Header = Loc.T("tray.offset.slower");
                offsetReset.Header = Loc.T("tray.offset.reset");
                startupToggle.Header = Loc.T("tray.startup");
                searchItem.Header = Loc.T("tray.search");
                editItem.Header = Loc.T("tray.edit");
                exportItem.Header = Loc.T("tray.export");
                wrongItem.Header = Loc.T("tray.wrong");
                settingsItem.Header = Loc.T("tray.settings");
                exitItem.Header = Loc.T("tray.exit");
                UpdateOffsetLabel();
                UpdateUpdateItemText();
                if (coordinator.CurrentTrack is null)
                {
                    trackItem.Header = Loc.T("status.noTrack");
                    tray.ToolTipText = Loc.T("tray.tooltip.version", ("version", updater.CurrentVersion.ToString()));
                }
            }
            Loc.CultureChanged += ApplyMenuText;

            coordinator.Start(); // 배선 완료 후 시작 (캐시/번역/상태 이벤트 유효)
            Log.Write("=== LyricsX 시작 (M5) ===");

            // 시작 시 백그라운드 업데이트 확인(비침습: 발견 시 트레이 메뉴 라벨만 갱신)
            _ = RunUpdateCheckAsync(userInitiated: false);
        };
        app.Run();
    }

    /// <summary>파일명으로 쓸 수 없는 문자를 '_'로 치환한다.</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>런타임 생성 트레이 아이콘: 녹색 원 + "L" (전용 .ico는 M5에서)</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x1D, 0xB9, 0x54));
        g.FillEllipse(brush, 1, 1, 30, 30);
        using var font = new System.Drawing.Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        using var format = new System.Drawing.StringFormat
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center,
        };
        g.DrawString("L", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 1, 32, 32), format);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
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
