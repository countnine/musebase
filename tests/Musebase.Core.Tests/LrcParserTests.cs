using Musebase.Core;
using Xunit;

namespace Musebase.Core.Tests;

public class LrcParserTests
{
    private const string SimpleLrc = """
        [ti:夜に駆ける]
        [ar:YOASOBI]
        [al:THE BOOK]
        [offset:150]
        [00:00.50]沈むように溶けてゆくように
        [00:06.15]二人だけの空が広がる夜に
        [00:13.00]
        [00:14.20]「さよなら」だけだった
        """;

    [Fact]
    public void Parse_SimpleLrc_ExtractsIdTagsAndLines()
    {
        var lyrics = Lyrics.Parse(SimpleLrc);

        Assert.NotNull(lyrics);
        Assert.Equal("夜に駆ける", lyrics.IdTags[Lyrics.TagTitle]);
        Assert.Equal("YOASOBI", lyrics.IdTags[Lyrics.TagArtist]);
        Assert.Equal(150, lyrics.Offset);
        Assert.Equal(0.15, lyrics.TimeDelay, 3);
        Assert.Equal(4, lyrics.Lines.Count);
        Assert.Equal(0.5, lyrics.Lines[0].Position, 3);
        Assert.Equal("沈むように溶けてゆくように", lyrics.Lines[0].Content);
        Assert.Equal("", lyrics.Lines[2].Content); // 빈 라인 유지
    }

    [Fact]
    public void Parse_CrlfInput_ParsesSameAsLf()
    {
        var crlf = SimpleLrc.Replace("\n", "\r\n");
        var lyrics = Lyrics.Parse(crlf);

        Assert.NotNull(lyrics);
        Assert.Equal(4, lyrics.Lines.Count);
        Assert.Equal("夜に駆ける", lyrics.IdTags[Lyrics.TagTitle]);
    }

    [Fact]
    public void Parse_MultipleTimeTagsOnOneLine_ExpandsToMultipleLines()
    {
        var lyrics = Lyrics.Parse("[00:10.00][01:20.50]반복되는 후렴");

        Assert.NotNull(lyrics);
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.Equal(10.0, lyrics.Lines[0].Position, 3);
        Assert.Equal(80.5, lyrics.Lines[1].Position, 3);
        Assert.All(lyrics.Lines, l => Assert.Equal("반복되는 후렴", l.Content));
    }

    [Fact]
    public void Parse_InlineBracketTranslation_BecomesTranslationAttachment()
    {
        var lyrics = Lyrics.Parse("[00:01.00]夜に駆ける【밤을 달리다】");

        Assert.NotNull(lyrics);
        Assert.Equal("夜に駆ける", lyrics.Lines[0].Content);
        Assert.Equal("밤을 달리다", lyrics.Lines[0].Attachments.Translation());
    }

    [Fact]
    public void Parse_AttachmentLines_AttachToMatchingLine()
    {
        var text = """
            [00:01.00]hello world
            [00:01.00][tr:ko]안녕 세상
            [00:01.00][tt]<0,0><500,6><1000,11>
            """;
        var lyrics = Lyrics.Parse(text);

        Assert.NotNull(lyrics);
        Assert.Single(lyrics.Lines);
        var line = lyrics.Lines[0];
        Assert.Equal("안녕 세상", line.Attachments.Translation("ko"));
        Assert.Equal("안녕 세상", line.Attachments.Translation()); // 후보 미지정 시 tr* 폴백

        var tt = line.Attachments.GetInlineTimeTags();
        Assert.NotNull(tt);
        Assert.Equal(3, tt.Tags.Count);
        Assert.Equal(0, tt.Tags[0].Index);
        Assert.Equal(0.5, tt.Tags[1].Time, 3);
        Assert.Equal(11, tt.Tags[2].Index);

        Assert.Contains("tr:ko", lyrics.Metadata.AttachmentTags);
        Assert.Contains("tt", lyrics.Metadata.AttachmentTags);
    }

    [Fact]
    public void Parse_NoLyricLines_ReturnsNull()
    {
        Assert.Null(Lyrics.Parse("[ti:제목만 있는 파일]"));
        Assert.Null(Lyrics.Parse("그냥 텍스트"));
        Assert.Null(Lyrics.Parse(""));
    }

    [Fact]
    public void LineIndexesAt_ReturnsCurrentAndNext()
    {
        var lyrics = Lyrics.Parse(SimpleLrc)!;

        // 첫 라인 전
        var (cur, next) = lyrics.LineIndexesAt(0.0);
        Assert.Null(cur);
        Assert.Equal(0, next);

        // 중간
        (cur, next) = lyrics.LineIndexesAt(7.0);
        Assert.Equal(1, cur);
        Assert.Equal(2, next);

        // 마지막 라인 후
        (cur, next) = lyrics.LineIndexesAt(999.0);
        Assert.Equal(3, cur);
        Assert.Null(next);

        // 정확히 라인 위치
        (cur, next) = lyrics.LineIndexesAt(6.15);
        Assert.Equal(1, cur);
    }

    [Fact]
    public void LineIndexesAt_SkipsDisabledLines()
    {
        var lyrics = Lyrics.Parse(SimpleLrc)!;
        lyrics.Lines[2].Enabled = false;

        var (cur, next) = lyrics.LineIndexesAt(13.5);
        Assert.Equal(1, cur);  // 비활성 라인(2) 건너뛰고 이전 활성 라인
        Assert.Equal(3, next); // 다음 활성 라인
    }

    [Fact]
    public void Filtrate_DisablesMatchingLines()
    {
        var lyrics = Lyrics.Parse("[00:01.00]QQ音乐 提供\n[00:05.00]진짜 가사")!;
        lyrics.Filtrate(l => !l.Content.Contains("提供"));

        Assert.False(lyrics.Lines[0].Enabled);
        Assert.True(lyrics.Lines[1].Enabled);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var text = """
            [ti:test]
            [00:01.00]hello
            [00:01.00][tr:ko]안녕
            [00:05.50]world
            """;
        var original = Lyrics.Parse(text)!;
        var reparsed = Lyrics.Parse(original.ToString());

        Assert.NotNull(reparsed);
        Assert.Equal(original.Lines.Count, reparsed.Lines.Count);
        Assert.Equal(original.IdTags[Lyrics.TagTitle], reparsed.IdTags[Lyrics.TagTitle]);
        for (var i = 0; i < original.Lines.Count; i++)
        {
            Assert.Equal(original.Lines[i].Content, reparsed.Lines[i].Content);
            Assert.Equal(original.Lines[i].Position, reparsed.Lines[i].Position, 3);
            Assert.Equal(original.Lines[i].Attachments, reparsed.Lines[i].Attachments);
        }
    }

    [Fact]
    public void ToLegacyString_InlinesTranslation()
    {
        var lyrics = Lyrics.Parse("[00:01.00]hello\n[00:01.00][tr]안녕")!;
        var legacy = lyrics.ToLegacyString();

        Assert.Contains("[00:01.000]hello【안녕】", legacy);
    }

    [Fact]
    public void Length_ParsesMinuteSecondFormat()
    {
        var lyrics = Lyrics.Parse("[length:3:45.5]\n[00:01.00]x")!;
        Assert.Equal(225.5, lyrics.Length!.Value, 3);
    }
}
