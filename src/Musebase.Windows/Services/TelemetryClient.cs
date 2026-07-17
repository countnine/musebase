using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Musebase.Engine;

namespace Musebase.Windows.Services;

/// <summary>
/// Windows 텔레메트리 클라이언트(ADR-0004, contracts/telemetry-events.md).
/// - 옵트인 2단계: ① 기본(<see cref="AppSettings.TelemetryBasicEnabled"/>) /
///   ② 품질(<see cref="AppSettings.TelemetryQualityEnabled"/>). 꺼져 있으면 수집 자체를 안 한다.
/// - 로컬 큐: %LOCALAPPDATA%\Musebase\telemetry-queue.jsonl (1줄=1이벤트, 상한 500건).
/// - 업로드: 시작 30초 후 + 이후 6시간마다 배치(≤100건) POST. 실패 시 큐 보존 후 재시도.
/// - <see cref="Track"/>은 논블로킹이며 절대 던지지 않는다(수집 실패는 무해).
/// - 클라이언트 집계: playback_source는 appId별 하루 1회 디바운스, feature_use는 세션 카운터로
///   모았다가 업로드 시 집계 이벤트로 변환, app_session은 하루 1회(일일 ping 겸용).
/// </summary>
public sealed class TelemetryClient : ITelemetry, IDisposable
{
    private const string IngestUrl = "https://musebase-telemetry.musebase.workers.dev/ingest";
    private const int MaxQueuedEvents = 500;   // 로컬 큐 상한(초과 시 오래된 것부터 삭제)
    private const int MaxEventsPerBatch = 100; // 서버 배치 상한
    private const int MaxBatchBytes = 60_000;  // 서버 본문 상한 64KB 이하로 여유

    /// <summary>② 품질 리포트 동의가 있어야 기록되는 이벤트 type(곡 제목/아티스트 포함).</summary>
    private static readonly HashSet<string> QualityOnlyTypes = new(StringComparer.Ordinal)
    {
        TelemetryEvents.LyricsNotFound,
        TelemetryEvents.WrongLyrics,
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly AppSettings _settings;
    private readonly Action<string>? _log;
    private readonly Func<IReadOnlyDictionary<string, object?>>? _appSessionProps;
    private readonly string _queuePath;
    private readonly string _statePath;
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly ConcurrentDictionary<string, int> _featureCounts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private DebounceState? _state; // 지연 로드(파일 IO는 첫 사용 시)

    /// <summary>디바운스 상태(%LOCALAPPDATA%\Musebase\telemetry-state.json). 날짜는 로컬 yyyy-MM-dd.</summary>
    private sealed class DebounceState
    {
        public string? AppSessionDate { get; set; }
        public string? PlaybackSourceDate { get; set; }
        public List<string> PlaybackSourceApps { get; set; } = new();
    }

    /// <param name="appSessionProps">
    /// app_session 이벤트 props 공급자(업로드 주기마다 하루 1회 체크 후 발화). null이면 미발화.
    /// </param>
    public TelemetryClient(
        AppSettings settings,
        Action<string>? log = null,
        Func<IReadOnlyDictionary<string, object?>>? appSessionProps = null)
    {
        _settings = settings;
        _log = log;
        _appSessionProps = appSessionProps;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musebase");
        _queuePath = Path.Combine(dir, "telemetry-queue.jsonl");
        _statePath = Path.Combine(dir, "telemetry-state.json");
    }

    // ---------------------------------------------------------------- ITelemetry

    /// <inheritdoc />
    public void Track(string type, IReadOnlyDictionary<string, object?>? props = null)
    {
        try
        {
            // 동의 게이트: ② 전용 type은 ②, 그 외는 ①. 꺼져 있으면 수집 자체를 안 한다.
            if (QualityOnlyTypes.Contains(type))
            {
                if (!_settings.TelemetryQualityEnabled) return;
            }
            else if (!_settings.TelemetryBasicEnabled)
            {
                return;
            }

            // feature_use: 세션 카운터로만 집계(업로드 시 집계 이벤트로 변환)
            if (type == TelemetryEvents.FeatureUse)
            {
                if (props?.TryGetValue("feature", out var f) == true && f is string feature && feature.Length > 0)
                    _featureCounts.AddOrUpdate(feature, 1, (_, c) => c + 1);
                return;
            }

            // playback_source: 같은 appId는 하루 1회
            if (type == TelemetryEvents.PlaybackSource)
            {
                if (props?.TryGetValue("appId", out var a) != true || a is not string appId || appId.Length == 0)
                    return;
                if (!TryMarkPlaybackSourceToday(appId)) return;
            }

            // app_session: 하루 1회(일일 ping 겸용)
            if (type == TelemetryEvents.AppSession && !TryMarkAppSessionToday())
                return;

            Enqueue(type, props);
        }
        catch
        {
            // 텔레메트리는 앱 동작에 절대 영향을 주지 않는다
        }
    }

    /// <summary>feature_use 카운트 편의 메서드(기존 핸들러에 한 줄 추가용).</summary>
    public void CountFeature(string feature) =>
        Track(TelemetryEvents.FeatureUse, new Dictionary<string, object?> { ["feature"] = feature });

    /// <summary>
    /// 비처리 예외 → error 이벤트. kind(예외 타입명)/frame(최상위 스택프레임 1개)/fatal만 —
    /// 메시지 본문·파일 경로는 절대 포함하지 않는다.
    /// </summary>
    public void TrackError(Exception ex, bool fatal)
    {
        try
        {
            var method = new StackTrace(ex).GetFrame(0)?.GetMethod();
            var frame = method is null ? "unknown" : $"{method.DeclaringType?.FullName}.{method.Name}";
            Track(TelemetryEvents.Error, new Dictionary<string, object?>
            {
                ["kind"] = ex.GetType().Name,
                ["frame"] = frame,
                ["fatal"] = fatal,
            });
        }
        catch
        {
            // 무시
        }
    }

    // ---------------------------------------------------------------- clientId

    /// <summary>동의가 처음 켜질 때 익명 clientId(랜덤 GUID)를 생성해 설정에 저장한다.</summary>
    public static void EnsureClientId(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TelemetryClientId))
        {
            settings.TelemetryClientId = Guid.NewGuid().ToString();
            settings.Save();
        }
    }

    /// <summary>익명 ID 재설정: 새 랜덤 GUID로 교체(설정 UI의 "익명 ID 재설정").</summary>
    public static void ResetClientId(AppSettings settings)
    {
        settings.TelemetryClientId = Guid.NewGuid().ToString();
        settings.Save();
    }

    // ---------------------------------------------------------------- 큐(JSONL)

    /// <summary>이벤트를 메모리 대기열에 넣고 백그라운드로 디스크에 붙인다(호출 스레드 논블로킹).</summary>
    private void Enqueue(string type, IReadOnlyDictionary<string, object?>? props)
    {
        var line = JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["type"] = type, ["props"] = props ?? new Dictionary<string, object?>() },
            JsonOptions);
        _pendingLines.Enqueue(line);
        _ = Task.Run(FlushPendingToDisk);
    }

    /// <summary>메모리 대기열을 큐 파일에 반영(상한 500건 유지). 치명 예외 핸들러에서 동기 호출 가능.</summary>
    public void FlushPendingToDisk()
    {
        try
        {
            lock (_fileLock)
            {
                if (_pendingLines.IsEmpty) return;
                var lines = new List<string>();
                if (File.Exists(_queuePath))
                    lines.AddRange(File.ReadAllLines(_queuePath).Where(l => !string.IsNullOrWhiteSpace(l)));
                while (_pendingLines.TryDequeue(out var line)) lines.Add(line);
                if (lines.Count > MaxQueuedEvents)
                    lines.RemoveRange(0, lines.Count - MaxQueuedEvents); // 오래된 것부터 삭제
                Directory.CreateDirectory(Path.GetDirectoryName(_queuePath)!);
                File.WriteAllLines(_queuePath, lines);
            }
        }
        catch
        {
            // 큐 기록 실패는 무해(이벤트 유실 허용)
        }
    }

    // ---------------------------------------------------------------- 업로더

    /// <summary>
    /// 백그라운드 업로더 시작: 초기 지연(기본 30초, 환경변수
    /// MUSEBASE_TELEMETRY_INITIAL_DELAY_SECONDS로 재정의 가능) 후 1회, 이후 6시간마다.
    /// </summary>
    public void StartUploader()
    {
        _ = Task.Run(async () =>
        {
            var initial = TimeSpan.FromSeconds(30);
            if (int.TryParse(Environment.GetEnvironmentVariable("MUSEBASE_TELEMETRY_INITIAL_DELAY_SECONDS"),
                    out var secs) && secs >= 0)
                initial = TimeSpan.FromSeconds(secs);
            try { await Task.Delay(initial, _cts.Token); } catch { return; }
            while (!_cts.IsCancellationRequested)
            {
                await UploadOnceAsync().ConfigureAwait(false);
                try { await Task.Delay(TimeSpan.FromHours(6), _cts.Token); } catch { return; }
            }
        });
    }

    /// <summary>업로드 1주기: 일일 app_session 발화 → feature_use 집계 반영 → 큐 배치 전송.</summary>
    public async Task UploadOnceAsync()
    {
        try
        {
            if (!_settings.TelemetryBasicEnabled && !_settings.TelemetryQualityEnabled) return;

            // 일일 app_session ping(하루 1회 — 자정 넘겨 계속 켜둔 세션도 커버)
            if (_settings.TelemetryBasicEnabled && _appSessionProps is not null && TryMarkAppSessionToday())
                Enqueue(TelemetryEvents.AppSession, _appSessionProps());

            DrainFeatureCounters();
            FlushPendingToDisk();

            EnsureClientId(_settings);
            var clientId = _settings.TelemetryClientId!;
            var appVersion = AppVersion();

            while (!_cts.IsCancellationRequested)
            {
                List<string> all;
                lock (_fileLock)
                {
                    if (!File.Exists(_queuePath)) return;
                    all = File.ReadAllLines(_queuePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                }
                if (all.Count == 0) return;

                // 배치 구성: ≤100건, 본문 ≤64KB. 손상 라인은 버린다.
                var batch = new List<string>();
                var taken = 0;
                var bytes = 0;
                foreach (var line in all)
                {
                    taken++;
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); } catch { continue; } // 손상 라인 폐기
                    if (node?["type"]?.GetValue<string>() is not { Length: > 0 }) continue;
                    var size = Encoding.UTF8.GetByteCount(line);
                    if (batch.Count > 0 && (batch.Count >= MaxEventsPerBatch || bytes + size > MaxBatchBytes))
                    {
                        taken--; // 이 라인은 다음 배치로
                        break;
                    }
                    batch.Add(node.ToJsonString(JsonOptions));
                    bytes += size;
                }

                if (batch.Count > 0)
                {
                    var body = $"{{\"clientId\":{JsonSerializer.Serialize(clientId)}," +
                               $"\"platform\":\"windows\"," +
                               $"\"appVersion\":{JsonSerializer.Serialize(appVersion)}," +
                               $"\"events\":[{string.Join(",", batch)}]}}";
                    using var resp = await Http.PostAsync(
                        IngestUrl, new StringContent(body, Encoding.UTF8, "application/json"),
                        _cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _log?.Invoke($"[telemetry] 업로드 실패(HTTP {(int)resp.StatusCode}) — 큐 보존, 다음 주기에 재시도");
                        return; // 실패: 큐 보존
                    }
                    _log?.Invoke($"[telemetry] 업로드 성공: {batch.Count}건 (HTTP {(int)resp.StatusCode})");
                }

                // 성공(또는 전부 손상 라인): 전송/폐기분을 큐에서 제거
                lock (_fileLock)
                {
                    var current = File.Exists(_queuePath)
                        ? File.ReadAllLines(_queuePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                        : new List<string>();
                    // 업로드 도중 추가된 이벤트는 보존: 처리한 앞부분(taken)만 제거
                    var remaining = current.Count > taken ? current.Skip(taken).ToList() : new List<string>();
                    if (remaining.Count == 0) File.Delete(_queuePath);
                    else File.WriteAllLines(_queuePath, remaining);
                }
                if (taken >= all.Count) return; // 큐 소진
            }
        }
        catch (Exception e)
        {
            _log?.Invoke($"[telemetry] 업로드 오류: {e.GetType().Name} — 큐 보존");
        }
    }

    /// <summary>세션 feature_use 카운터를 집계 이벤트로 변환해 대기열에 넣는다.</summary>
    private void DrainFeatureCounters()
    {
        foreach (var feature in _featureCounts.Keys.ToList())
        {
            if (_featureCounts.TryRemove(feature, out var count) && count > 0)
                Enqueue(TelemetryEvents.FeatureUse, new Dictionary<string, object?>
                {
                    ["feature"] = feature,
                    ["count"] = count,
                });
        }
    }

    private static string AppVersion() =>
        typeof(TelemetryClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---------------------------------------------------------------- 디바운스 상태

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    private DebounceState LoadState()
    {
        if (_state is not null) return _state;
        try
        {
            if (File.Exists(_statePath))
                _state = JsonSerializer.Deserialize<DebounceState>(File.ReadAllText(_statePath));
        }
        catch
        {
            // 손상 상태 파일은 초기화
        }
        return _state ??= new DebounceState();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch
        {
            // 무시
        }
    }

    /// <summary>오늘 app_session을 아직 안 보냈으면 기록하고 true.</summary>
    private bool TryMarkAppSessionToday()
    {
        lock (_fileLock)
        {
            var s = LoadState();
            var today = Today();
            if (s.AppSessionDate == today) return false;
            s.AppSessionDate = today;
            SaveState();
            return true;
        }
    }

    /// <summary>오늘 이 appId의 playback_source를 아직 안 보냈으면 기록하고 true.</summary>
    private bool TryMarkPlaybackSourceToday(string appId)
    {
        lock (_fileLock)
        {
            var s = LoadState();
            var today = Today();
            if (s.PlaybackSourceDate != today)
            {
                s.PlaybackSourceDate = today;
                s.PlaybackSourceApps.Clear();
            }
            if (s.PlaybackSourceApps.Contains(appId, StringComparer.OrdinalIgnoreCase)) return false;
            s.PlaybackSourceApps.Add(appId);
            SaveState();
            return true;
        }
    }

    // ---------------------------------------------------------------- 종료

    /// <summary>업로더 중지 + 세션 카운터/대기열을 디스크로 보존(다음 실행에서 업로드).</summary>
    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            DrainFeatureCounters();
            FlushPendingToDisk();
            _cts.Dispose();
        }
        catch
        {
            // 무시
        }
    }
}
