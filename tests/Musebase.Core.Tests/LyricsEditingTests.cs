using Musebase.Core;
using Xunit;

namespace Musebase.Core.Tests;

public class LyricsEditingTests
{
    private static Lyrics MakeMultiLang()
    {
        var tt = new InlineTimeTags([new(0, 0.0), new(5, 0.5)], 1.0).ToString();
        var att = new LineAttachments
        {
            ["tr:ko"] = "안녕",
            ["tr:ja"] = "こんにちは",
            [LineAttachments.TagTimeTag] = tt,
        };
        var lyrics = new Lyrics([new LyricsLine("Hello", 1.0, att)]);
        lyrics.IdTags[Lyrics.TagTitle] = "Song";
        return lyrics;
    }

    [Fact]
    public void TranslationTags_ListsPresentLanguagesPlusGeneric()
    {
        var tags = LyricsEditing.TranslationTags(MakeMultiLang());
        Assert.Contains("tr", tags);      // generic 항상 포함
        Assert.Contains("tr:ko", tags);
        Assert.Contains("tr:ja", tags);
    }

    [Fact]
    public void ToSimpleText_ShowsOnlySelectedLanguage()
    {
        Assert.Equal("[00:01.000]Hello【안녕】", LyricsEditing.ToSimpleText(MakeMultiLang(), "tr:ko"));
        Assert.Equal("[00:01.000]Hello【こんにちは】", LyricsEditing.ToSimpleText(MakeMultiLang(), "tr:ja"));
    }

    [Fact]
    public void ApplySimpleEdit_UpdatesSelectedLangAndPreservesRest()
    {
        var original = MakeMultiLang();
        // ko 번역과 원문을 수정
        var edited = "[00:01.000]Hi【반가워】";

        var result = LyricsEditing.ApplySimpleEdit(original, edited, "tr:ko");

        Assert.NotNull(result);
        var line = result!.Lines[0];
        Assert.Equal("Hi", line.Content);                          // 원문 반영
        Assert.Equal("반가워", line.Attachments["tr:ko"]);          // 선택 언어 반영
        Assert.Equal("こんにちは", line.Attachments["tr:ja"]);      // 타 언어 보존
        Assert.NotNull(line.Attachments.GetInlineTimeTags());       // tt 보존
        Assert.Equal("Song", result.IdTags[Lyrics.TagTitle]);      // ID 태그 보존
    }

    [Fact]
    public void ApplySimpleEdit_RemovingBracketsClearsThatLanguage()
    {
        var result = LyricsEditing.ApplySimpleEdit(MakeMultiLang(), "[00:01.000]Hello", "tr:ko");
        Assert.NotNull(result);
        Assert.Null(result!.Lines[0].Attachments["tr:ko"]);        // 【】 제거 → 해당 언어 번역 삭제
        Assert.Equal("こんにちは", result.Lines[0].Attachments["tr:ja"]); // 타 언어는 유지
    }
}
