using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Musebase.Windows.Services;

/// <summary>
/// 전경 창이 전체화면(모니터 전체 커버)인지 1초 간격으로 감시한다.
/// 게임/영상 전체화면 시 오버레이를 숨기기 위한 신호원.
/// 원본 macOS의 CGWindowList 전체화면 감지에 해당.
///
/// <b>오버레이가 있는 모니터만 본다.</b> 예전에는 아무 모니터에서나 전체화면이면 숨겼는데,
/// 듀얼 모니터에서 왼쪽에 RDP를 전체화면으로 띄우면 **오른쪽 모니터의 가사창이 사라졌다** —
/// 가리지도 않는 창 때문에 숨는 셈이라 고장으로 보인다. 같은 모니터의 전체화면은 실제로
/// 오버레이를 덮으므로 그대로 숨긴다.
/// </summary>
public sealed partial class FullscreenDetector : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<IntPtr>? _overlayHandle;
    private bool _lastFullscreen;

    /// <summary>전체화면 상태 변경 (true = 오버레이와 같은 모니터에 전체화면 앱 활성)</summary>
    public event Action<bool>? FullscreenChanged;

    public bool IsFullscreen => _lastFullscreen;

    /// <param name="overlayHandle">
    /// 오버레이 창 핸들을 그때그때 알려 준다(사람이 창을 다른 모니터로 옮길 수 있으므로
    /// 값을 캐시하지 않는다). null이거나 아직 만들어지지 않았으면 예전처럼 모니터를 가리지 않는다 —
    /// 모르는 상태에서 안 숨기는 쪽으로 틀리면 게임 위에 가사가 올라간다.
    /// </param>
    public FullscreenDetector(Dispatcher dispatcher, Func<IntPtr>? overlayHandle = null)
    {
        _overlayHandle = overlayHandle;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        var fullscreen = DetectFullscreen();
        if (fullscreen != _lastFullscreen)
        {
            _lastFullscreen = fullscreen;
            FullscreenChanged?.Invoke(fullscreen);
        }
    }

    private bool DetectFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        // 자기 자신(오버레이 등)은 제외
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return false;

        // 바탕화면/셸 창 제외
        var className = new StringBuilder(64);
        _ = GetClassName(hwnd, className, className.Capacity);
        var cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(hwnd, out var rect)) return false;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        // 창이 모니터 전체를 덮지 않으면 전체화면이 아니다.
        if (!CoversMonitor(rect, info.rcMonitor)) return false;

        // 오버레이가 다른 모니터에 있으면 가려지지 않는다 — 숨길 이유가 없다.
        return OnSameMonitorAsOverlay(monitor);
    }

    /// <summary>창이 그 모니터를 빈틈없이 덮는가.</summary>
    private static bool CoversMonitor(RECT window, RECT monitor) =>
        window.Left <= monitor.Left
        && window.Top <= monitor.Top
        && window.Right >= monitor.Right
        && window.Bottom >= monitor.Bottom;

    /// <summary>
    /// 전체화면 창의 모니터가 오버레이의 모니터와 같은가.
    /// 핸들을 알 수 없으면 <c>true</c> — 판단이 안 될 때는 숨기는 쪽이 안전하다(예전 동작).
    /// </summary>
    private bool OnSameMonitorAsOverlay(IntPtr fullscreenMonitor)
    {
        var overlay = _overlayHandle?.Invoke() ?? IntPtr.Zero;
        if (overlay == IntPtr.Zero) return true;

        var overlayMonitor = MonitorFromWindow(overlay, MONITOR_DEFAULTTONEAREST);
        return overlayMonitor == IntPtr.Zero || overlayMonitor == fullscreenMonitor;
    }

    public void Dispose() => _timer.Stop();

    // ---- P/Invoke ----

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
}
