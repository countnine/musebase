using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Musebase.Windows.Services;

/// <summary>
/// 전경 창이 전체화면(모니터 전체 커버)인지 1초 간격으로 감시한다.
/// 게임/영상 전체화면 시 오버레이를 숨기기 위한 신호원.
/// 원본 macOS의 CGWindowList 전체화면 감지에 해당.
/// </summary>
public sealed partial class FullscreenDetector : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _lastFullscreen;

    /// <summary>전체화면 상태 변경 (true = 전체화면 앱 활성)</summary>
    public event Action<bool>? FullscreenChanged;

    public bool IsFullscreen => _lastFullscreen;

    public FullscreenDetector(Dispatcher dispatcher)
    {
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

    private static bool DetectFullscreen()
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

        // 창이 모니터 전체를 덮으면 전체화면으로 판정
        return rect.Left <= info.rcMonitor.Left
            && rect.Top <= info.rcMonitor.Top
            && rect.Right >= info.rcMonitor.Right
            && rect.Bottom >= info.rcMonitor.Bottom;
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
