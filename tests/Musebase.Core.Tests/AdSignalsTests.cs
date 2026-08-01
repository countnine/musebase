using Musebase.Engine;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 광고 판정(<see cref="AdSignals"/>). Windows(SMTC)와 Android(MediaSession)가 같은 규칙을 쓰는데,
/// 오탐은 **진짜 곡의 가사가 안 뜨는** 결과로 이어지므로 폴백의 안전장치를 특히 본다.
/// </summary>
public class AdSignalsTests
{
    [Fact]
    public void 표준_광고_플래그가_있으면_다른_신호를_보지_않는다()
    {
        Assert.True(AdSignals.LooksLikeAd(1, null, "실제 아티스트", "실제 앨범"));
    }

    [Fact]
    public void Spotify_광고_미디어ID는_광고다()
    {
        Assert.True(AdSignals.LooksLikeAd(0, "spotify:ad:d892a38", "광고 • 1/2", ""));
        Assert.False(AdSignals.LooksLikeAd(0, "spotify:track:abc", "MGMT", "Oracular Spectacular"));
    }

    /// <summary>Windows에는 플래그·mediaId가 없어 이 폴백만 남는다(실측 검증된 형태).</summary>
    [Fact]
    public void 윈도우_폴백은_아티스트가_Spotify이고_앨범이_비었을_때만_광고다()
    {
        Assert.True(AdSignals.LooksLikeAd("Spotify", ""));
        Assert.True(AdSignals.LooksLikeAd("Sponsored Message", null));
        // 앨범이 있으면 진짜 곡이다 — "Spotify"라는 이름의 밴드가 있어도 가사를 막지 않는다.
        Assert.False(AdSignals.LooksLikeAd("Spotify", "Some Album"));
    }

    [Fact]
    public void 곡_전환_순간의_빈_메타데이터는_광고가_아니다()
    {
        // 아티스트가 비는 순간이 있는데, 이걸 광고로 보면 곡마다 가사가 한 박자씩 늦는다.
        Assert.False(AdSignals.LooksLikeAd("", ""));
        Assert.False(AdSignals.LooksLikeAd(null, null));
    }

    [Fact]
    public void 평범한_곡은_광고가_아니다()
    {
        Assert.False(AdSignals.LooksLikeAd(0, "spotify:track:xyz", "Phoenix", "Wolfgang Amadeus Phoenix"));
        Assert.False(AdSignals.LooksLikeAd("Phoenix", ""));
    }
}
