using System.IO.Compression;
using System.Text;
using Musebase.Core;
using Musebase.Core.Search;
using Xunit;

namespace Musebase.Core.Tests;

// ---- 압축/블록 테스트 유틸 ----
internal static class CryptoTestUtil
{
    public static byte[] ZlibCompress(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(bytes, 0, bytes.Length);
        return ms.ToArray();
    }

    public static byte[] Pad8(byte[] d)
    {
        var n = (d.Length + 7) / 8 * 8;
        var r = new byte[n];
        Array.Copy(d, r, d.Length);
        return r;
    }
}

public class KugouKrcTests
{
    private static readonly byte[] DecodeKey =
        [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];

    /// <summary>"krc1" + XOR(zlib(plaintext)) 블롭을 만들어 복호가 원문을 복원하는지 검증</summary>
    private static byte[] BuildKrcBlob(string plaintext)
    {
        var compressed = CryptoTestUtil.ZlibCompress(plaintext);
        var blob = new byte[4 + compressed.Length];
        Encoding.ASCII.GetBytes("krc1").CopyTo(blob, 0);
        for (var i = 0; i < compressed.Length; i++)
            blob[4 + i] = (byte)(compressed[i] ^ DecodeKey[i & 0b1111]);
        return blob;
    }

    [Fact]
    public void Decrypt_RoundTripsXorAndZlib()
    {
        const string krc = "[ti:테스트곡]\n[0,2000]<0,500,0>안녕<500,500,0>하<1000,1000,0>세요";
        var decrypted = KugouKrcDecrypter.Decrypt(BuildKrcBlob(krc));
        Assert.Equal(krc, decrypted);
    }

    [Fact]
    public void Decrypt_RejectsWithoutMagic()
    {
        Assert.Null(KugouKrcDecrypter.Decrypt(Encoding.ASCII.GetBytes("nope-not-krc")));
    }

    [Fact]
    public void KrcParser_ParsesInlineTagsAndContent()
    {
        const string krc = "[ti:테스트곡]\n[ar:가수]\n[0,2000]<0,500,0>안녕<500,500,0>하<1000,1000,0>세요";
        var lyrics = KrcParser.Parse(krc);

        Assert.NotNull(lyrics);
        Assert.Equal("테스트곡", lyrics!.IdTags[Lyrics.TagTitle]);
        Assert.Single(lyrics.Lines);
        Assert.Equal("안녕하세요", lyrics.Lines[0].Content);
        Assert.Equal(0.0, lyrics.Lines[0].Position);

        var inline = lyrics.Lines[0].Attachments.GetInlineTimeTags();
        Assert.NotNull(inline);
        Assert.Equal(2.0, inline!.Duration); // 2000ms
        Assert.Equal(4, inline.Tags.Count);  // 초기(0,0) + 3 fragment
    }

    [Fact]
    public void KrcParser_MergesLanguageHeaderTranslation()
    {
        const string json = """{"content":[{"language":0,"type":1,"lyricContent":[["안녕하세요"]]}],"version":1}""";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var krc = $"[language:{b64}]\n[0,2000]<0,2000,0>Hello";

        var lyrics = KrcParser.Parse(krc);
        Assert.NotNull(lyrics);
        Assert.Equal("Hello", lyrics!.Lines[0].Content);
        Assert.Equal("안녕하세요", lyrics.Lines[0].Attachments.Translation());
        Assert.DoesNotContain("language", (IEnumerable<string>)lyrics.IdTags.Keys);
    }
}

public class QQMusicQrcTests
{
    private static readonly byte[] Key1 = Encoding.ASCII.GetBytes("!@#)(NHLiuy*$%^&");
    private static readonly byte[] Key2 = Encoding.ASCII.GetBytes("123ZXC!@#)(*$%^&");
    private static readonly byte[] Key3 = Encoding.ASCII.GetBytes("!@#)(*$%^&abcDEF");

    [Fact]
    public void Des_DecryptInvertsEncrypt()
    {
        var data = new byte[16];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 3);
        var original = (byte[])data.Clone();

        QQMusicQrcDecrypter.Des(data, Key1, data.Length);
        Assert.NotEqual(original, data);
        QQMusicQrcDecrypter.Ddes(data, Key1, data.Length);
        Assert.Equal(original, data);
    }

    /// <summary>복호 역순(des K3 → ddes K2 → des K1)으로 hex를 만들어 Decode가 원문을 복원하는지 검증</summary>
    private static string BuildQrcHex(string plaintext)
    {
        var buf = CryptoTestUtil.Pad8(CryptoTestUtil.ZlibCompress(plaintext));
        QQMusicQrcDecrypter.Des(buf, Key3, buf.Length);
        QQMusicQrcDecrypter.Ddes(buf, Key2, buf.Length);
        QQMusicQrcDecrypter.Des(buf, Key1, buf.Length);
        return Convert.ToHexString(buf);
    }

    [Fact]
    public void Decode_RoundTripsTripleDesAndZlib()
    {
        const string qrc = "[ti:노래]\n[0,3000]Hello(0,1000) World(1000,2000)";
        Assert.Equal(qrc, QQMusicQrcDecrypter.Decode(BuildQrcHex(qrc)));
    }

    [Fact]
    public void QrcParser_ParsesInlineTagsAndContent()
    {
        const string qrc = "[ti:노래]\n[0,3000]Hello(0,1000) World(1000,2000)";
        var lyrics = QrcParser.Parse(qrc);

        Assert.NotNull(lyrics);
        Assert.Equal("노래", lyrics!.IdTags[Lyrics.TagTitle]);
        Assert.Single(lyrics.Lines);
        Assert.Equal("Hello World", lyrics.Lines[0].Content);

        var inline = lyrics.Lines[0].Attachments.GetInlineTimeTags();
        Assert.NotNull(inline);
        Assert.Equal(3.0, inline!.Duration);
    }

    [Fact]
    public void DecodeSingleLyricText_ExtractsNestedLyricContent()
    {
        const string xml =
            "<?xml version=\"1.0\"?><QrcInfos><LyricInfo>" +
            "<Lyric_1 LyricType=\"1\" LyricContent=\"[0,1000]Hi(0,1000)\"/>" +
            "</LyricInfo></QrcInfos>";
        var decoded = QQMusicProvider.DecodeSingleLyricText(xml);
        Assert.NotNull(decoded);
        Assert.Contains("[0,1000]", decoded);
        Assert.Contains("Hi", decoded);
    }
}
