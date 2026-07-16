using System.Text.Json;
using Musebase.Engine;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>PlaybackViewState 직렬화 계약(원격 디스플레이 브로드캐스트용).</summary>
public class PlaybackViewStateTests
{
    [Fact]
    public void RoundTrips_ThroughJson_PreservingKaraokeAnchors()
    {
        var state = new PlaybackViewState(
            IsPlaying: true,
            TrackTitle: "曲名", TrackArtist: "歌手",
            LineContent: "沈むように", LineTranslation: "가라앉듯이",
            Karaoke: new[] { new KaraokeMark(0, 0.0), new KaraokeMark(3, 0.5) },
            KaraokeDurationSeconds: 2.5,
            LineStartedAt: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            LineSpanSeconds: 3.0,
            CanPrevious: true, CanPlayPause: true, CanNext: false);

        var json = JsonSerializer.Serialize(state);
        var back = JsonSerializer.Deserialize<PlaybackViewState>(json);

        Assert.NotNull(back);
        // 재직렬화 일치 = 전 필드 무손실 왕복(레코드 값 동등성은 리스트 멤버를 참조 비교하므로 사용 불가)
        Assert.Equal(json, JsonSerializer.Serialize(back));
        Assert.Equal(state.IsPlaying, back!.IsPlaying);
        Assert.Equal(state.TrackTitle, back.TrackTitle);
        Assert.Equal(state.LineTranslation, back.LineTranslation);
        Assert.Equal(state.LineStartedAt, back.LineStartedAt);
        Assert.Equal(2, back.Karaoke!.Count);
        Assert.Equal(3, back.Karaoke[1].Index);
        Assert.Equal(0.5, back.Karaoke[1].Time);
    }

    [Fact]
    public void Empty_HasNoLine_AndNotPlaying()
    {
        Assert.False(PlaybackViewState.Empty.IsPlaying);
        Assert.Null(PlaybackViewState.Empty.LineContent);
        Assert.Null(PlaybackViewState.Empty.Karaoke);
    }
}
