// Musebase for Windows (구 LyricsX) — 엔트리포인트 (오버레이 + 트레이 제어)

using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Musebase.Windows.Overlay;
using Musebase.Windows.Services;
using Musebase.Browser;
using Musebase.Engine;
using Velopack;

namespace Musebase.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack: 설치/업데이트/제거 훅 처리. 반드시 다른 앱 로직보다 먼저 실행해야 한다.
        VelopackApp.Build().Run();

        // 개명(LyricsX→Musebase) 데이터 이전 — 설정/캐시/로그를 읽기 전에 수행해야 한다.
        MigrateLegacyAppData();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var settings = AppSettings.Load();
            Loc.Initialize(settings.UiLanguage); // UI 다국어: 창 생성 전에 언어 확정
            var nowPlaying = await NowPlayingService.CreateAsync();

            // ---- 텔레메트리(익명 옵트인, ADR-0004) ----
            // 동의가 꺼져 있으면 Track이 즉시 무시(수집 자체 안 함). 큐·업로드는 클라이언트가 관리.
            var telemetry = new TelemetryClient(settings, Log.Write, appSessionProps: () =>
                new Dictionary<string, object?>
                {
                    ["uiLang"] = Loc.CurrentCode,
                    ["targetLang"] = settings.EffectiveTargetLanguage,
                    ["engine"] = settings.EffectiveTranslationEngine,
                    ["sources"] = settings.EnabledLyricsSources.ToArray(),
                    ["sourceMode"] = settings.PlaybackSource,
                    ["osVersion"] = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10",
                });
            telemetry.StartUploader();

            // 비처리 예외 → error 이벤트(kind/frame/fatal만 — 메시지 본문·경로 금지)
            app.DispatcherUnhandledException += (_, e) =>
            {
                telemetry.TrackError(e.Exception, fatal: true);
                telemetry.FlushPendingToDisk(); // 프로세스 종료 전 큐 보존(다음 실행에서 업로드)
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) telemetry.TrackError(ex, e.IsTerminating);
                telemetry.FlushPendingToDisk();
            };

            // 재생 소스 앱 통계는 코디네이터(엔진 계측)가 발화하고, 클라이언트가 appId별 하루 1회로 디바운스한다.

            // 번역: SQLite 라인 캐시 + 레지스트리에서 선택된 엔진(키 없으면 무키 무료로 폴백)
            var cacheDb = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Musebase", "translations.db");
            var translationCache = new Musebase.Core.Translation.SqliteTranslationCache(cacheDb);

            // 설정 → 플랫폼 무관 엔진 구성(소스/엔진/키/캐시). 저장 시 최신값으로 다시 읽는다.
            EngineConfig CurrentConfig() => new(
                settings.EnabledLyricsSources,
                settings.EffectiveTranslationEngine,
                new Musebase.Core.Translation.TranslatorOptions(
                    DeeplApiKey: settings.DeeplApiKey,
                    LibreEndpoint: settings.LibreTranslateEndpoint),
                settings.EffectiveTargetLanguage,
                settings.ShowOnlyTargetTranslation,
                settings.ManualOffsetSeconds,
                cacheDb,
                // 폴백 켬 + 주 엔진이 무키 무료 기본이 아닐 때만 무료 엔진(MyMemory)으로 자동 전환.
                TranslationFallbackEngineId:
                    settings.TranslationFallbackToFree
                    && !string.Equals(settings.EffectiveTranslationEngine, Musebase.Core.Translation.TranslatorRegistry.DefaultFreeEngine, StringComparison.OrdinalIgnoreCase)
                        ? Musebase.Core.Translation.TranslatorRegistry.DefaultFreeEngine : null);

            Log.Write($"[sources] 활성 가사 소스: {string.Join(", ", settings.EnabledLyricsSources)}");
            Log.Write($"[translate] 엔진={settings.EffectiveTranslationEngine}");

            // 트레이/미니창은 아래에서 생성되지만, 번역 실패 콜백·표시 동기화가 이들을 참조하므로 선언만 먼저.
            H.NotifyIcon.TaskbarIcon? tray = null;
            MiniWindow? miniWindow = null;

            // 브라우저 디스플레이 서버(인프로세스 Kestrel). null=꺼짐. 토글/자동시작/종료 경로가 공유.
            BrowserDisplayServer? browserServer = null;

            // 번역 실패 사용자 힌트: 같은 kind는 세션 1회만(곡마다 스팸 금지). 로그는 매 실패 기록.
            var reportedFailureKinds = new HashSet<Musebase.Core.Translation.TranslatorFailureKind>();
            static string LocalizeFailureHint(Musebase.Core.Translation.TranslatorFailureKind kind) => kind switch
            {
                Musebase.Core.Translation.TranslatorFailureKind.Quota => Loc.T("translate.fail.quota"),
                Musebase.Core.Translation.TranslatorFailureKind.Auth => Loc.T("translate.fail.auth"),
                Musebase.Core.Translation.TranslatorFailureKind.RateLimit => Loc.T("translate.fail.rate"),
                Musebase.Core.Translation.TranslatorFailureKind.Server => Loc.T("translate.fail.server"),
                Musebase.Core.Translation.TranslatorFailureKind.Network => Loc.T("translate.fail.network"),
                _ => Loc.T("translate.fail.other"),
            };
            void OnTranslationFailure(Musebase.Core.Translation.TranslatorFailure f)
            {
                Log.Write($"[translate] 실패: engine={f.EngineId} status={(f.HttpStatus?.ToString() ?? "-")} kind={f.Kind}");
                if (!reportedFailureKinds.Add(f.Kind)) return; // 세션당 kind 1회만 사용자 힌트
                var hint = LocalizeFailureHint(f.Kind);
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (tray is { } t) t.ToolTipText = Loc.T("tray.tooltip.status", ("status", hint));
                    miniWindow?.SetStatus(hint);
                });
            }

            // 공유 조합 팩토리로 코디네이터 조립(동일 조합을 Android/서버가 재사용)
            var coordinator = LyricsEngineFactory.Create(
                nowPlaying, new WpfEngineDispatcher(app.Dispatcher), CurrentConfig(), translationCache, Log.Write,
                telemetry: telemetry, // 엔진 계측(lyrics_search/translation/wrong_lyrics/…) 활성화
                onTranslationFailure: OnTranslationFailure); // 번역 실패 로깅·힌트

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
                // 트레이·미니창과 같은 코드 경로(MediaPrevious/MediaPlayPause/MediaNext)를 공유.
                overlay.EnableMediaControls(
                    controlsProvider: () => nowPlaying.GetControls(),
                    playingProvider: () => nowPlaying.IsPlaying,
                    onPrevious: MediaPrevious,
                    onPlayPause: MediaPlayPause,
                    onNext: MediaNext);

                // 재생 상태 변경 시 미니창 재생 버튼(재생/일시정지·활성) 갱신.
                nowPlaying.IsPlayingChanged += _ =>
                    app.Dispatcher.BeginInvoke(() => miniWindow?.RefreshPlayback());
            }

            // ---- 트레이 메뉴 ----
            var trackItem = new MenuItem { Header = Loc.T("status.noTrack"), IsEnabled = false };
            var overlayToggle = new MenuItem { Header = Loc.T("tray.overlay.show"), IsCheckable = true, IsChecked = settings.OverlayVisible };
            var moveToggle = new MenuItem { Header = Loc.T("tray.overlay.moveMode"), IsCheckable = true };
            var sourceMenu = new MenuItem { Header = Loc.T("tray.source") };
            var browserDisplayToggle = new MenuItem { Header = Loc.T("tray.browserDisplay"), IsCheckable = true, IsChecked = settings.BrowserDisplayEnabled };

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
            // ---- 트레이·미니창 공유 동작(중복 구현 방지: 같은 로컬 함수를 호출) ----
            LyricsEditorWindow? editorWindow = null;
            void MediaPrevious() { telemetry.CountFeature("mediaControls"); _ = nowPlaying.SkipPreviousAsync(); }
            void MediaPlayPause() { telemetry.CountFeature("mediaControls"); _ = nowPlaying.TogglePlayPauseAsync(); }
            void MediaNext() { telemetry.CountFeature("mediaControls"); _ = nowPlaying.SkipNextAsync(); }
            bool HasLyrics() => coordinator.CurrentLyrics is not null && coordinator.CurrentTrack is not null;
            void OpenSearch()
            {
                telemetry.CountFeature("search");
                new SearchWindow(coordinator).Show();
            }
            void OpenLyricsEditor()
            {
                if (coordinator.CurrentLyrics is not { } lyrics || coordinator.CurrentTrack is not { } track) return;
                telemetry.CountFeature("edit");
                if (editorWindow is { IsLoaded: true })
                {
                    editorWindow.Activate();
                    return;
                }
                editorWindow = new LyricsEditorWindow(
                    track.ToString(), lyrics,
                    edited => coordinator.SaveEditedLyrics(track, edited));
                editorWindow.Show();
            }
            void MarkWrong() => coordinator.MarkWrongLyrics();

            var searchItem = new MenuItem { Header = Loc.T("tray.search") };
            searchItem.Click += (_, _) => OpenSearch();

            // ---- 현재 가사 편집 / 내보내기 ----
            var editItem = new MenuItem { Header = Loc.T("tray.edit") };
            var exportItem = new MenuItem { Header = Loc.T("tray.export") };
            editItem.Click += (_, _) => OpenLyricsEditor();

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
                telemetry.CountFeature("export");
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
                    MessageBox.Show(Loc.T("export.fail", ("error", ex.Message)), "Musebase", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            // 현재 가사가 틀렸을 때: 표시 중단 + 캐시 제거 + 재검색 억제
            var wrongItem = new MenuItem { Header = Loc.T("tray.wrong") };
            wrongItem.Click += (_, _) => MarkWrong();

            var openMiniItem = new MenuItem { Header = Loc.T("mini.open") };
            openMiniItem.Click += (_, _) => miniWindow?.ShowFromTray();

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
            void OpenSettings()
            {
                if (settingsWindow is { IsLoaded: true })
                {
                    settingsWindow.Activate();
                    return;
                }
                settingsWindow = new SettingsWindow(settings, onSaved: () =>
                {
                    var cfg = CurrentConfig();
                    reportedFailureKinds.Clear(); // 엔진/폴백 변경 시 힌트를 다시 안내할 수 있게 초기화
                    coordinator.Translation = LyricsEngineFactory.BuildTranslation(cfg, translationCache, OnTranslationFailure);
                    coordinator.TargetLanguage = cfg.TargetLanguage;
                    coordinator.ShowOnlyTargetTranslation = cfg.ShowOnlyTargetTranslation;
                    coordinator.RefreshCurrentLine(); // 표시 정책 변경 즉시 반영
                    overlay.ApplyStyle();
                    Log.Write($"[settings] 저장됨: engine={cfg.TranslationEngineId}, lang={cfg.TargetLanguage}, fallback={cfg.TranslationFallbackEngineId ?? "-"}");
                },
                onCheckUpdates: () => _ = RunUpdateCheckAsync(userInitiated: true));
                settingsWindow.Show();
            }
            settingsItem.Click += (_, _) => OpenSettings();

            void UpdateOffsetLabel() =>
                offsetLabel.Header = Loc.T("tray.offset.label", ("value", coordinator.ManualOffsetSeconds.ToString("+0.0;-0.0;0")));
            UpdateOffsetLabel();

            // 오버레이 표시 상태를 트레이·미니창과 동기화하며 설정에 저장.
            void SetOverlayVisible(bool visible)
            {
                settings.OverlayVisible = visible;
                overlay.SetUserVisible(visible);
                settings.Save();
                overlayToggle.IsChecked = visible;
                miniWindow?.SyncOverlayVisible(visible);
            }
            // 미니창(작업표시줄)에서 오버레이 되살리기 — 사용자 숨김/일시정지/가림방지 억제를 해제.
            void ReviveOverlay()
            {
                overlay.ReviveVisible();
                settings.OverlayVisible = true;
                settings.Save();
                overlayToggle.IsChecked = true;
                miniWindow?.SyncOverlayVisible(true);
            }
            // 종료(트레이/미니창 공통): 미니창 실제 닫힘 허용 후 앱 종료.
            void ExitApp()
            {
                miniWindow?.CloseForExit();
                app.Shutdown(); // 브라우저 서버 정리는 app.Exit 핸들러에서(설정은 유지 → 다음 실행 자동 시작)
            }

            // ---- 브라우저 디스플레이(태블릿/TV) 수명주기 ----
            // 실제 접속용 URL(LAN이면 사설 IPv4, 아니면 localhost) + 실제 바인딩 포트(포트=0 대응).
            string BrowserDisplayUrl(BrowserDisplayServer server)
            {
                var port = settings.BrowserDisplayPort;
                var bound = server.Urls
                    .Select(u => Uri.TryCreate(
                        u.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").Replace("+", "localhost"),
                        UriKind.Absolute, out var uri) ? uri : null)
                    .FirstOrDefault(u => u is not null);
                if (bound is not null) port = bound.Port;
                var host = settings.BrowserDisplayLan && LocalLanIPv4() is { } ip ? ip : "localhost";
                return $"http://{host}:{port}";
            }

            async Task StartBrowserDisplayAsync(bool userInitiated)
            {
                if (browserServer is not null) return; // 이미 실행 중
                try
                {
                    browserServer = await BrowserDisplayServer.StartAsync(new BrowserDisplayOptions(
                        Port: settings.BrowserDisplayPort,
                        ListenLan: settings.BrowserDisplayLan,
                        Log: Log.Write));
                    coordinator.StateChanged += browserServer.Publish; // 표시 상태 방송 연결
                    browserServer.Publish(coordinator.CurrentState);    // 접속 전이라도 현재 상태 1회 반영
                    telemetry.CountFeature("browserDisplay");

                    var url = BrowserDisplayUrl(browserServer);
                    settings.BrowserDisplayEnabled = true;
                    settings.Save();
                    browserDisplayToggle.IsChecked = true;
                    Log.Write($"[browser] 시작: 리슨={string.Join(", ", browserServer.Urls)} 안내URL={url}");

                    // URL 노출: 트레이 툴팁·미니창 상태 + (사용자 조작 시) 안내 다이얼로그.
                    var statusText = Loc.T("browserDisplay.status.on", ("url", url));
                    if (tray is { } t) t.ToolTipText = Loc.T("tray.tooltip.status", ("status", statusText));
                    miniWindow?.SetStatus(statusText);
                    if (userInitiated)
                        MessageBox.Show(
                            Loc.T("browserDisplay.dialog.body", ("url", url)),
                            Loc.T("browserDisplay.dialog.title"),
                            MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception e)
                {
                    Log.Write($"[browser] 시작 실패: {e.Message}");
                    if (browserServer is { } failed)
                    {
                        coordinator.StateChanged -= failed.Publish;
                        try { await failed.DisposeAsync(); } catch { /* 정리 실패 무시 */ }
                        browserServer = null;
                    }
                    settings.BrowserDisplayEnabled = false;
                    settings.Save();
                    browserDisplayToggle.IsChecked = false;
                    MessageBox.Show(
                        Loc.T("browserDisplay.fail", ("port", settings.BrowserDisplayPort.ToString()), ("error", e.Message)),
                        Loc.T("browserDisplay.dialog.title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            async Task StopBrowserDisplayAsync(bool save)
            {
                if (browserServer is not { } server) return;
                coordinator.StateChanged -= server.Publish;
                browserServer = null;
                try { await server.DisposeAsync(); }
                catch (Exception e) { Log.Write($"[browser] 정지 실패: {e.Message}"); }
                if (save)
                {
                    settings.BrowserDisplayEnabled = false;
                    settings.Save();
                }
                browserDisplayToggle.IsChecked = false;
                Log.Write("[browser] 정지");
            }

            browserDisplayToggle.Click += async (_, _) =>
            {
                if (browserDisplayToggle.IsChecked) await StartBrowserDisplayAsync(userInitiated: true);
                else await StopBrowserDisplayAsync(save: true);
            };

            overlayToggle.Click += (_, _) => SetOverlayVisible(overlayToggle.IsChecked);
            moveToggle.Click += (_, _) => overlay.SetMoveMode(moveToggle.IsChecked);
            overlay.MoveModeChanged += moveMode => moveToggle.IsChecked = moveMode; // 자물쇠 버튼과 동기화
            offsetPlus.Click += (_, _) => AdjustOffset(0.5);
            offsetMinus.Click += (_, _) => AdjustOffset(-0.5);
            offsetReset.Click += (_, _) => AdjustOffset(null);
            exitItem.Click += (_, _) => ExitApp();

            void AdjustOffset(double? delta)
            {
                telemetry.CountFeature("offset");
                coordinator.ManualOffsetSeconds = delta is { } d ? coordinator.ManualOffsetSeconds + d : 0;
                settings.ManualOffsetSeconds = coordinator.ManualOffsetSeconds;
                settings.Save();
                UpdateOffsetLabel();
                miniWindow?.RefreshOffset();
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
            menu.Items.Add(browserDisplayToggle);
            menu.Items.Add(new Separator());
            menu.Items.Add(offsetLabel);
            menu.Items.Add(offsetPlus);
            menu.Items.Add(offsetMinus);
            menu.Items.Add(offsetReset);
            menu.Items.Add(new Separator());
            menu.Items.Add(startupToggle);
            menu.Items.Add(updateItem);
            menu.Items.Add(openMiniItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(exitItem);

            var appIcon = CreateTrayIcon(); // 트레이·미니창이 공유하는 런타임 아이콘(녹색 원 + M)
            tray = new TaskbarIcon
            {
                Icon = appIcon,
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

            // ---- 작업표시줄 상주 미니창(컨트롤 허브) ----
            // 콜백은 위 트레이 로컬 함수를 그대로 주입 → 트레이/미니창이 같은 코드 경로를 공유.
            miniWindow = new MiniWindow(appIcon, new MiniWindowActions(
                IsOverlayVisible: () => settings.OverlayVisible,
                SetOverlayVisible: SetOverlayVisible,
                ReviveOverlay: ReviveOverlay,
                OpenSettings: OpenSettings,
                Exit: ExitApp,
                GetControls: () => nowPlaying.GetControls(),
                IsPlaying: () => nowPlaying.IsPlaying,
                OnPrevious: MediaPrevious,
                OnPlayPause: MediaPlayPause,
                OnNext: MediaNext,
                AdjustOffset: AdjustOffset,
                GetOffset: () => coordinator.ManualOffsetSeconds,
                OpenSearch: OpenSearch,
                OpenLyricsEditor: OpenLyricsEditor,
                MarkWrong: MarkWrong,
                HasLyrics: HasLyrics,
                CloseToTray: () => settings.MiniWindowCloseToTray));
            miniWindow.SetTrack(coordinator.CurrentTrack?.Title, coordinator.CurrentTrack?.Artist);
            if (coordinator.CurrentStatus is { } cs) miniWindow.SetStatus(LocalizeStatus(cs));
            miniWindow.RefreshLyricsFeatures();
            // 포커스를 뺏지 않도록 최소화 상태로 작업표시줄에 상주(오버레이는 별도로 표시됨).
            miniWindow.WindowState = WindowState.Minimized;
            miniWindow.Show();

            // 트레이 아이콘 더블클릭 → 미니창 복귀(닫기→트레이 옵션 사용 시 필수 경로).
            tray.TrayLeftMouseDoubleClick += (_, _) => miniWindow?.ShowFromTray();

            app.Exit += (_, _) =>
            {
                // 브라우저 서버 정리: 종료 직전 Kestrel을 멈춰 포트를 해제(설정은 유지 → 다음 실행 자동 시작).
                if (browserServer is { } bs)
                {
                    try { bs.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
                    catch { /* 종료 정리 실패 무시 */ }
                    browserServer = null;
                }
                tray.Dispose();
                appIcon.Dispose();
                telemetry.Dispose(); // 세션 feature_use 카운터를 큐로 보존(다음 실행에서 업로드)
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

            // 번역 표시 상태(정상/캐시/한도초과 등)를 소스·품질 텍스트 옆에 붙일 접미사.
            static string TranslationSuffix(TranslationDisplayStatus s)
            {
                var key = s switch
                {
                    TranslationDisplayStatus.Translating => "translation.status.translating",
                    TranslationDisplayStatus.Live => "translation.status.live",
                    TranslationDisplayStatus.Cache => "translation.status.cache",
                    TranslationDisplayStatus.Quota => "translation.status.quota",
                    TranslationDisplayStatus.Failed => "translation.status.failed",
                    _ => null, // None → 표기 안 함
                };
                return key is null ? "" : Loc.T("translation.status.suffix", ("status", Loc.T(key)));
            }

            // 가사 상태 + 번역 상태를 합쳐 트레이/미니창에 표시(둘 중 하나만 바뀌어도 재렌더).
            void RenderStatus()
            {
                var text = (coordinator.CurrentStatus is { } cs ? LocalizeStatus(cs) : "")
                    + TranslationSuffix(coordinator.CurrentTranslationStatus);
                trackItem.Header = text;
                if (tray is { } t) t.ToolTipText = Loc.T("tray.tooltip.status", ("status", text));
                miniWindow?.SetStatus(text);
            }

            coordinator.StatusChanged += status =>
            {
                RenderStatus();
                if (miniWindow is { } mw)
                {
                    mw.SetTrack(coordinator.CurrentTrack?.Title, coordinator.CurrentTrack?.Artist);
                    mw.RefreshLyricsFeatures();
                    mw.RefreshPlayback();
                }
                Log.Write($"[status] {LocalizeStatus(status)}");
            };

            // 번역 상태만 바뀌어도(예: 검색 후 번역 완료·한도초과) 소스 옆 표기를 갱신.
            coordinator.TranslationStatusChanged += _ => app.Dispatcher.BeginInvoke(RenderStatus);
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
                browserDisplayToggle.Header = Loc.T("tray.browserDisplay");
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
                openMiniItem.Header = Loc.T("mini.open");
                UpdateOffsetLabel();
                UpdateUpdateItemText();
                if (coordinator.CurrentTrack is null)
                {
                    trackItem.Header = Loc.T("status.noTrack");
                    if (tray is { } t) t.ToolTipText = Loc.T("tray.tooltip.version", ("version", updater.CurrentVersion.ToString()));
                }
                // 미니창의 곡/상태 텍스트도 새 언어로 다시 현지화(버튼 라벨은 MiniWindow가 자체 처리).
                if (miniWindow is { } mw)
                {
                    mw.SetTrack(coordinator.CurrentTrack?.Title, coordinator.CurrentTrack?.Artist);
                    if (coordinator.CurrentStatus is { } cs2) mw.SetStatus(LocalizeStatus(cs2));
                }
            }
            Loc.CultureChanged += ApplyMenuText;

            coordinator.Start(); // 배선 완료 후 시작 (캐시/번역/상태 이벤트 유효)
            Log.Write("=== Musebase 시작 ===");

            // 브라우저 디스플레이 자동 시작(설정 켜짐 시). 실패해도 앱 다른 기능엔 영향 없음.
            if (settings.BrowserDisplayEnabled)
                _ = StartBrowserDisplayAsync(userInitiated: false);

            // 텔레메트리 동의 다이얼로그(최초 1회, 기본 모두 꺼짐 = 미수집)
            if (!settings.TelemetryConsentAsked)
                new TelemetryConsentWindow(settings).Show();

            // 시작 시 백그라운드 업데이트 확인(비침습: 발견 시 트레이 메뉴 라벨만 갱신)
            _ = RunUpdateCheckAsync(userInitiated: false);
        };
        app.Run();
    }

    /// <summary>
    /// 이 PC의 사설대역 IPv4 주소(LAN 접속용). 여러 개면 첫 번째. 없으면 null(→ 호출부는 localhost 사용).
    /// 10.x / 172.16–31.x / 192.168.x 만 후보로 삼아 공인/링크로컬 주소를 배제한다.
    /// </summary>
    private static string? LocalLanIPv4()
    {
        try
        {
            foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var b = ip.GetAddressBytes();
                var isPrivate =
                    b[0] == 10 ||
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                    (b[0] == 192 && b[1] == 168);
                if (isPrivate) return ip.ToString();
            }
        }
        catch
        {
            // 주소 조회 실패 시 localhost 폴백
        }
        return null;
    }

    /// <summary>파일명으로 쓸 수 없는 문자를 '_'로 치환한다.</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>
    /// 개명(LyricsX→Musebase) 데이터 이전: 구 폴더(%LOCALAPPDATA%\LyricsX)가 있고
    /// 새 폴더가 없으면 통째로 이동해 설정·캐시·로그를 보존한다.
    /// </summary>
    private static void MigrateLegacyAppData()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldDir = Path.Combine(local, "LyricsX");
            var newDir = Path.Combine(local, "Musebase");
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                Directory.Move(oldDir, newDir);
        }
        catch
        {
            // 이전 실패(구 앱 실행 중 등) 시 새 폴더로 클린 시작 — 구 폴더는 그대로 남는다.
        }
    }

    /// <summary>런타임 생성 트레이 아이콘: 녹색 원 + "M" (전용 .ico 추후)</summary>
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
        g.DrawString("M", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 1, 32, 32), format);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>경량 파일 로그 (%LOCALAPPDATA%\Musebase\app.log)</summary>
internal static class Log
{
    private static readonly string LogPath = InitPath();
    private static readonly object Sync = new();

    private static string InitPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musebase");
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
