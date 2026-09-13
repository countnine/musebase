using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Shell;

namespace Musebase.Windows.Services;

/// <summary>
/// 작업표시줄 아이콘 우클릭 메뉴(점프 목록) + 그 항목이 <b>이미 돌고 있는 앱</b>에 닿게 하는 통로.
///
/// 점프 목록 항목은 트레이 메뉴와 달리 앱 안의 핸들러를 직접 부를 수 없다 — Windows가
/// <b>새 프로세스를 인자와 함께 실행</b>한다. 그래서 두 가지가 함께 필요하다.
/// ① 단일 인스턴스 판정(이미 돌고 있으면 둘째 프로세스는 명령만 넘기고 끝낸다)
/// ② 이름 있는 파이프로 그 명령을 첫 인스턴스에 전달
///
/// 파이프는 <b>현재 사용자만</b> 쓸 수 있는 로컬 파이프다(같은 세션의 같은 사용자).
/// </summary>
public static class TaskbarCommands
{
    /// <summary>점프 목록이 넘기는 명령 이름. 문자열이 계약이라 한곳에 모아 둔다.</summary>
    public const string Panel = "panel";
    public const string Overlay = "overlay";
    public const string Search = "search";
    public const string Meaning = "meaning";
    public const string Settings = "settings";
    public const string Exit = "exit";

    private const string PipeName = "Musebase.Commands.v1";
    private const string MutexName = @"Local\Musebase.SingleInstance.v1";

    private static Mutex? _instanceLock;

    /// <summary>
    /// 이 프로세스가 <b>첫 인스턴스</b>인가. 아니면 <paramref name="command"/>를 먼저 돌고 있는
    /// 쪽에 넘긴다(실패해도 조용히 — 앱을 못 띄우는 것보다 낫다).
    /// </summary>
    public static bool ClaimInstance(string? command)
    {
        try
        {
            _instanceLock = new Mutex(initiallyOwned: true, MutexName, out var first);
            if (first) return true;
        }
        catch (Exception)
        {
            return true; // 판정 자체가 실패하면 평소대로 뜬다
        }

        if (!string.IsNullOrEmpty(command)) Send(command!);
        return false;
    }

    /// <summary>명령을 첫 인스턴스에 넘긴다.</summary>
    private static void Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(command);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception)
        {
            // 먼저 돌던 앱이 막 종료된 경우 등 — 알릴 곳이 없으니 넘긴다.
        }
    }

    /// <summary>
    /// 명령 수신을 시작한다(첫 인스턴스에서 한 번). 한 번에 한 연결만 받으며,
    /// 받은 명령은 <paramref name="onCommand"/>로 넘긴다(UI 스레드 마샬링은 호출자 책임).
    /// </summary>
    public static void Listen(Action<string> onCommand)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var command = (await reader.ReadToEndAsync()).Trim();
                    if (command.Length > 0) onCommand(command);
                }
                catch (Exception e)
                {
                    Log.Write($"[taskbar] 명령 수신 실패: {e.Message}");
                    await Task.Delay(1000); // 되풀이 폭주 방지
                }
            }
        });
    }

    /// <summary>
    /// 작업표시줄 우클릭 메뉴를 만든다. 트레이 메뉴와 <b>같은 항목</b>을 고른다 —
    /// 켜짐/꺼짐이 있는 것(오버레이)은 점프 목록이 상태를 표시할 수 없어 "전환"으로 둔다.
    /// </summary>
    public static void InstallJumpList()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            foreach (var (command, key, glyph) in new[]
            {
                (Panel, "mini.open", "▣"),
                (Overlay, "taskbar.overlay", "▤"),
                (Search, "tray.search", "🔍"),
                (Meaning, "tray.meaning", "✦"),
                (Settings, "tray.settings", "⚙"),
                (Exit, "tray.exit", "✕"),
            })
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = Loc.T(key),
                    ApplicationPath = exe,
                    Arguments = "--command " + command,
                    // 아이콘은 직접 만든다 — Windows DLL의 아이콘 번호는 버전마다 달라
                    // 엉뚱한 그림이 붙을 수 있다(shell32.dll,13 같은 값에 기댈 수 없다).
                    IconResourcePath = JumpIcons.Make(command, glyph) ?? exe,
                    CustomCategory = Loc.T("taskbar.category"),
                });
            }

            list.Apply();
        }
        catch (Exception e)
        {
            // 점프 목록은 부가 기능이다 — 실패해도 앱은 그대로 돌아야 한다.
            Log.Write($"[taskbar] 점프 목록 등록 실패: {e.Message}");
        }
    }

    /// <summary><c>--command &lt;이름&gt;</c>을 뽑는다(없으면 null).</summary>
    public static string? ParseCommand(string[] args)
    {
        var index = Array.IndexOf(args, "--command");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
