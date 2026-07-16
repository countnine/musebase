using Musebase.Engine;

namespace Musebase.Browser;

/// <summary>
/// <c>--demo</c> 모드: 샘플 가사 4줄(카라오케 마크 포함, 마지막 줄은 마크 없이 단색 검증용)을
/// 5초 간격으로 순환 방송하고, 한 사이클에 1회 <c>IsPlaying=false</c> 상태를 끼워
/// 디스플레이의 숨김(페이드) 동작을 검증할 수 있게 한다.
/// 가사는 저작권 문제가 없는 자작 데모 문구다.
/// </summary>
public static class DemoLoop
{
    private const double SpanSeconds = 5.0;
    private const string DemoTitle = "Demo Song";
    private const string DemoArtist = "Musebase";

    private sealed record DemoLine(
        string Content, string? Translation, KaraokeMark[]? Karaoke, double? KaraokeDuration);

    private static readonly DemoLine[] Lines =
    [
        new("Shine on through the silent night",
            "고요한 밤을 지나 계속 빛나줘",
            [new(0, 0.0), new(6, 0.7), new(9, 1.3), new(17, 2.1), new(21, 2.7), new(28, 3.6)],
            4.2),
        new("Every word you sing becomes a star",
            "네가 부르는 모든 말이 별이 되어",
            [new(0, 0.0), new(6, 0.6), new(11, 1.1), new(15, 1.7), new(20, 2.5), new(30, 3.4)],
            4.0),
        new("가사가 흐르는 이 순간을 기억해",
            "Remember this moment as the lyrics flow",
            [new(0, 0.0), new(4, 0.8), new(8, 1.6), new(10, 2.0), new(14, 2.8)],
            4.0),
        // Karaoke = null → 계약 규칙 3: 줄 전체 단색 표시 검증용.
        new("We drift along the melody line",
            "우리는 멜로디를 따라 흘러가",
            null,
            null),
    ];

    /// <summary>취소될 때까지 데모 상태를 순환 방송한다.</summary>
    public static async Task RunAsync(StateBroadcaster broadcaster, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (var i = 0; i < Lines.Length; i++)
                {
                    broadcaster.Publish(ToState(Lines[i]));
                    await Task.Delay(TimeSpan.FromSeconds(SpanSeconds), cancellationToken)
                        .ConfigureAwait(false);

                    if (i == 1)
                    {
                        // 사이클마다 1회: 일시정지 상태 → 오버레이 전체 숨김 검증.
                        broadcaster.Publish(new PlaybackViewState(
                            IsPlaying: false,
                            TrackTitle: DemoTitle,
                            TrackArtist: DemoArtist,
                            LineContent: null,
                            LineTranslation: null,
                            Karaoke: null,
                            KaraokeDurationSeconds: null,
                            LineStartedAt: null,
                            LineSpanSeconds: 0,
                            CanPrevious: true,
                            CanPlayPause: true,
                            CanNext: true));
                        await Task.Delay(TimeSpan.FromSeconds(SpanSeconds), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 서버 종료 — 정상.
        }
    }

    private static PlaybackViewState ToState(DemoLine line) => new(
        IsPlaying: true,
        TrackTitle: DemoTitle,
        TrackArtist: DemoArtist,
        LineContent: line.Content,
        LineTranslation: line.Translation,
        Karaoke: line.Karaoke,
        KaraokeDurationSeconds: line.KaraokeDuration,
        LineStartedAt: DateTimeOffset.UtcNow,
        LineSpanSeconds: SpanSeconds,
        CanPrevious: true,
        CanPlayPause: true,
        CanNext: true);
}
