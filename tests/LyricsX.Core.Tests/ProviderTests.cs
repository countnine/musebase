using System.Text;
using System.Text.Json;
using LyricsX.Core;
using LyricsX.Core.Search;
using Xunit;

namespace LyricsX.Core.Tests;

public class MergeTests
{
    private static Lyrics Make(params (double Pos, string Content)[] lines) =>
        new(lines.Select(l => new LyricsLine(l.Content, l.Pos)));

    [Fact]
    public void MergeTranslation_MatchesByTimeTag()
    {
        var lyrics = Make((1.0, "hello"), (5.0, "world"), (9.0, "bye"));
        var translation = Make((1.0, "안녕"), (5.01, "세상"), (12.0, "무시됨"));

        lyrics.MergeTranslation(translation);

        Assert.Equal("안녕", lyrics.Lines[0].Attachments.Translation());
        Assert.Equal("세상", lyrics.Lines[1].Attachments.Translation()); // 0.02 임계 내
        Assert.Null(lyrics.Lines[2].Attachments.Translation());
    }

    [Fact]
    public void MergeTranslation_SkipsPlaceholderAndEmpty()
    {
        var lyrics = Make((1.0, "a"), (2.0, "b"));
        var translation = Make((1.0, "//"), (2.0, ""));

        lyrics.MergeTranslation(translation);

        Assert.Null(lyrics.Lines[0].Attachments.Translation());
        Assert.Null(lyrics.Lines[1].Attachments.Translation());
    }

    [Fact]
    public void ForceMergeTranslation_RequiresEqualCount()
    {
        var lyrics = Make((1.0, "a"), (2.0, "b"));
        var mismatch = Make((1.0, "x"));
        lyrics.ForceMergeTranslation(mismatch);
        Assert.Null(lyrics.Lines[0].Attachments.Translation());

        var exact = Make((7.7, "가"), (8.8, "나")); // 타임태그 무관
        lyrics.ForceMergeTranslation(exact);
        Assert.Equal("가", lyrics.Lines[0].Attachments.Translation());
        Assert.Equal("나", lyrics.Lines[1].Attachments.Translation());
    }
}

public class NetEaseEapiTests
{
    [Fact]
    public void AesEcb_RoundTrips()
    {
        var data = Encoding.UTF8.GetBytes("nobody/api/song/lyric/v1use{}md5forencrypt");
        var encrypted = NetEaseEapiClient.AesEncryptEcb(data);
        var decrypted = NetEaseEapiClient.AesDecryptEcb(encrypted);
        Assert.Equal(data, decrypted);
        Assert.NotEqual(data, encrypted.Take(data.Length));
    }

    [Fact]
    public void BuildEapiParams_ProducesUppercaseHex_AndDecryptsToSignedMessage()
    {
        var payload = new Dictionary<string, string> { ["id"] = "12345", ["lv"] = "0" };
        var hex = NetEaseEapiClient.BuildEapiParams(
            "https://interface3.music.163.com/eapi/song/lyric/v1", payload);

        Assert.Matches("^[0-9A-F]+$", hex);

        var decrypted = Encoding.UTF8.GetString(
            NetEaseEapiClient.AesDecryptEcb(Convert.FromHexString(hex)));

        // "{path}-36cd479b6b5-{json}-36cd479b6b5-{md5}" 구조 확인
        var parts = decrypted.Split("-36cd479b6b5-");
        Assert.Equal(3, parts.Length);
        Assert.Equal("/api/song/lyric/v1", parts[0]);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, string>>(parts[1]);
        Assert.Equal("12345", roundTripped!["id"]);
        Assert.Matches("^[0-9a-f]{32}$", parts[2]);
    }
}

public class LrclibParsingTests
{
    [Fact]
    public void Record_DeserializesFromApiJson()
    {
        const string json = """
            {"id":123,"trackName":"Test","artistName":"Artist","albumName":"Album",
             "duration":213.5,"instrumental":false,"plainLyrics":"a\nb",
             "syncedLyrics":"[00:01.00]a\n[00:05.00]b"}
            """;
        var record = JsonSerializer.Deserialize<LrclibProvider.Record>(json);

        Assert.NotNull(record);
        Assert.Equal(123, record.Id);
        Assert.Equal(213.5, record.Duration);
        Assert.NotNull(record.SyncedLyrics);
        Assert.NotNull(Lyrics.Parse(record.SyncedLyrics!));
    }
}
