using Musebase.Core;
using Musebase.Core.Search;
using Xunit;

namespace Musebase.Core.Tests;

public class NetEaseParserTests
{
    [Fact]
    public void ParseYrc_BuildsContentAndInlineTags()
    {
        // 라인 시작 1000ms, 두 단어: (1000,500)"Hel" (1500,500)"lo"
        const string yrc = "[1000,1000](1000,500,0)Hel(1500,500,0)lo";
        var lyrics = NetEaseLyricParser.ParseYrc(yrc);

        Assert.NotNull(lyrics);
        Assert.Single(lyrics!.Lines);
        Assert.Equal("Hello", lyrics.Lines[0].Content);
        Assert.Equal(1.0, lyrics.Lines[0].Position);

        var inline = lyrics.Lines[0].Attachments.GetInlineTimeTags();
        Assert.NotNull(inline);
        // "Hel" 끝(1000-1000+500=500ms→0.5s @ index3), "lo" 끝(1500-1000+500=1000ms→1.0s @ index5)
        Assert.Contains(inline!.Tags, t => t.Index == 3 && Math.Abs(t.Time - 0.5) < 1e-9);
        Assert.Contains(inline.Tags, t => t.Index == 5 && Math.Abs(t.Time - 1.0) < 1e-9);
        Assert.Contains(LineAttachments.TagTimeTag, lyrics.Metadata.AttachmentTags);
    }

    [Fact]
    public void ParseKLyric_AccumulatesDurationsAndHandlesSpace()
    {
        // 지속시간 누적: (0,300)"Hi" 이후 (0,200)"there" + (0,1) 공백
        const string klyric = "[2000,1000](0,300)Hi(0,200)there(0,1) ";
        var lyrics = NetEaseLyricParser.ParseKLyric(klyric);

        Assert.NotNull(lyrics);
        Assert.Single(lyrics!.Lines);
        Assert.Equal("Hithere ", lyrics.Lines[0].Content); // 마지막 (0,1)로 공백 추가
        Assert.Equal(2.0, lyrics.Lines[0].Position);

        var inline = lyrics.Lines[0].Attachments.GetInlineTimeTags();
        Assert.NotNull(inline);
        // 누적: "Hi"@0.3s, "there"@0.5s(+0.001은 공백 처리분)
        Assert.Contains(inline!.Tags, t => t.Index == 2 && Math.Abs(t.Time - 0.3) < 1e-9);
    }

    [Fact]
    public void ParseYrc_ReturnsNullWhenNoLines()
    {
        Assert.Null(NetEaseLyricParser.ParseYrc("{\"t\":0,\"c\":[]}"));
    }
}
