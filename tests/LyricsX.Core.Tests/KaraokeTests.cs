using LyricsX.Core;
using Xunit;

namespace LyricsX.Core.Tests;

public class InlineKaraokeTests
{
    // "가나다" 3글자: 0s→0, 1s→글자1, 2s→글자2, 3s→글자3
    private static readonly InlineTimeTags Tags = new(
        [new(0, 0.0), new(1, 1.0), new(2, 2.0), new(3, 3.0)], 3.0);

    [Fact]
    public void CharIndexAt_ClampsBeforeStartAndAfterEnd()
    {
        Assert.Equal(0, Tags.CharIndexAt(-1.0));
        Assert.Equal(0, Tags.CharIndexAt(0.0));
        Assert.Equal(3, Tags.CharIndexAt(5.0)); // 마지막 태그 이후 고정
    }

    [Fact]
    public void CharIndexAt_InterpolatesWithinSegment()
    {
        Assert.Equal(0.5, Tags.CharIndexAt(0.5), 3); // 0↔1 중간
        Assert.Equal(1.0, Tags.CharIndexAt(1.0), 3);
        Assert.Equal(2.25, Tags.CharIndexAt(2.25), 3); // 2↔3 구간 1/4
    }

    [Fact]
    public void CharIndexAt_EmptyTagsReturnsZero()
    {
        var empty = new InlineTimeTags([], null);
        Assert.Equal(0, empty.CharIndexAt(10.0));
    }
}
