// M0-S1: SMTC(now-playing) 스파이크
// 목표: 현재 재생 곡 메타(제목/아티스트/앨범), 재생 상태, 타임라인 위치 취득과
//       트랙 변경/상태 변경 이벤트 수신을 확인한다.
// 관찰 포인트: 플레이어별 타임라인 갱신 주기(Spotify는 수 초 간격) → 위치 보간 필요 여부 판단.

using Windows.Media.Control;

using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

var manager = await SessionManager.RequestAsync();

Console.WriteLine("=== 현재 SMTC 세션 목록 ===");
foreach (var s in manager.GetSessions())
    Console.WriteLine($"  - {s.SourceAppUserModelId}");

var current = manager.GetCurrentSession();
if (current is null)
{
    Console.WriteLine("현재 세션 없음 — 음악 앱에서 재생을 시작한 뒤 다시 실행하세요.");
}

manager.CurrentSessionChanged += (_, _) =>
{
    var s = manager.GetCurrentSession();
    Console.WriteLine($"[세션 변경] {s?.SourceAppUserModelId ?? "(없음)"}");
    if (s is not null) Hook(s);
};

if (current is not null) Hook(current);

Console.WriteLine();
Console.WriteLine("60초간 1초 간격으로 상태를 폴링합니다. (Ctrl+C 종료)");
for (var i = 0; i < 60; i++)
{
    await Task.Delay(1000);
    var s = manager.GetCurrentSession();
    if (s is null) continue;
    await PrintSnapshotAsync(s);
}

static void Hook(Session session)
{
    session.MediaPropertiesChanged += async (s, _) =>
    {
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            Console.WriteLine($"[트랙 변경] {p.Artist} - {p.Title} ({p.AlbumTitle})");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[트랙 변경] 메타 취득 실패: {e.Message}");
        }
    };
    session.PlaybackInfoChanged += (s, _) =>
        Console.WriteLine($"[상태 변경] {s.GetPlaybackInfo().PlaybackStatus}");
    session.TimelinePropertiesChanged += (s, _) =>
    {
        var t = s.GetTimelineProperties();
        Console.WriteLine($"[타임라인] pos={t.Position:mm\\:ss\\.fff} / end={t.EndTime:mm\\:ss} (updated {t.LastUpdatedTime:HH:mm:ss.fff})");
    };
}

static async Task PrintSnapshotAsync(Session session)
{
    try
    {
        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();

        // SMTC 타임라인은 앱에 따라 드물게 갱신된다.
        // LastUpdatedTime 기준 경과분을 더해 실제 재생 위치를 보간한다.
        var position = timeline.Position;
        if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(30))
                position += elapsed;
        }

        Console.WriteLine(
            $"{playback.PlaybackStatus,-8} {props.Artist} - {props.Title} " +
            $"| raw={timeline.Position:mm\\:ss\\.fff} interp={position:mm\\:ss\\.fff} / {timeline.EndTime:mm\\:ss}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"스냅샷 실패: {e.Message}");
    }
}
