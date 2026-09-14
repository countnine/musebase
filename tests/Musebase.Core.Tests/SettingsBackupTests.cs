using System.Text.Json;
using Musebase.Core.Settings;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 설정 백업. 지켜야 할 것은 둘이다 —
/// ① <b>비밀값이 실제로 옮겨질 것</b>(플랫폼 잠금은 다른 PC에서 안 풀리므로 이 기능의 존재 이유다),
/// ② <b>틀린 비밀번호로는 아무것도 넘어오지 않을 것</b>(어중간하게 반영되면 되돌릴 방법이 없다).
/// </summary>
public class SettingsBackupTests
{
    private const string SettingsJson =
        """{"targetLanguage":"KO","overlayFontFamily":"Segoe UI","deeplApiKeyEnc":"BASE64-DPAPI"}""";

    [Fact]
    public void 비밀번호로_잠근_키가_그대로_돌아온다()
    {
        var secrets = new SettingsBackup.Secrets(DeeplApiKey: "dk-1234", LyricsServerToken: "server-token");

        var json = SettingsBackup.Export(SettingsJson, secrets, "열쇠말");
        var opened = SettingsBackup.Import(json, "열쇠말");

        Assert.Equal("dk-1234", opened.Secrets!.DeeplApiKey);
        Assert.Equal("server-token", opened.Secrets.LyricsServerToken);
    }

    [Fact]
    public void 평문_설정도_함께_옮겨진다()
    {
        var opened = SettingsBackup.Import(
            SettingsBackup.Export(SettingsJson, new SettingsBackup.Secrets(), null), null);

        Assert.Equal("KO", opened.Settings.GetProperty("targetLanguage").GetString());
        Assert.Equal("Segoe UI", opened.Settings.GetProperty("overlayFontFamily").GetString());
    }

    [Fact]
    public void 비밀번호가_틀리면_아무것도_넘어오지_않는다()
    {
        var json = SettingsBackup.Export(
            SettingsJson, new SettingsBackup.Secrets(DeeplApiKey: "dk-1234"), "맞는말");

        // 예외가 나야 한다 — 평문 설정만 돌려주면 호출자가 어중간한 상태를 만든다.
        var error = Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, "틀린말"));
        Assert.Contains("비밀번호", error.Message);
    }

    [Fact]
    public void 비밀번호가_빠지면_풀지_않는다()
    {
        var json = SettingsBackup.Export(
            SettingsJson, new SettingsBackup.Secrets(DeeplApiKey: "dk-1234"), "말");

        Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, null));
    }

    [Fact]
    public void 비밀번호가_필요한_백업인지_미리_알려_준다()
    {
        var withSecrets = new SettingsBackup.Secrets(DeeplApiKey: "dk-1");

        Assert.True(SettingsBackup.NeedsPassword(SettingsBackup.Export(SettingsJson, withSecrets, "말")));
        Assert.False(SettingsBackup.NeedsPassword(SettingsBackup.Export(SettingsJson, withSecrets, null)));
        Assert.False(SettingsBackup.NeedsPassword("그냥 글자"));
    }

    [Fact]
    public void 비밀번호_없이_내보내면_키가_담기지_않는다()
    {
        // 잠그지 않은 키를 파일에 흘리느니 빼고 내보낸다.
        var json = SettingsBackup.Export(
            SettingsJson, new SettingsBackup.Secrets(DeeplApiKey: "dk-secret-1234"), null);

        Assert.DoesNotContain("dk-secret-1234", json);
        Assert.Null(SettingsBackup.Import(json, null).Secrets);
    }

    [Fact]
    public void 평문_키가_파일에_그대로_남지_않는다()
    {
        var json = SettingsBackup.Export(
            SettingsJson,
            new SettingsBackup.Secrets(DeeplApiKey: "dk-secret-1234", LyricsServerToken: "tok-secret-9876"),
            "말");

        Assert.DoesNotContain("dk-secret-1234", json);
        Assert.DoesNotContain("tok-secret-9876", json);
    }

    [Fact]
    public void 이_PC에서만_풀리는_암호문은_옮기지_않는다()
    {
        // *Enc는 DPAPI(CurrentUser)라 다른 PC에서 못 읽는다 — 담아 봐야 쓸모가 없고 오해만 만든다.
        var json = SettingsBackup.Export(SettingsJson, new SettingsBackup.Secrets(), null);

        Assert.DoesNotContain("BASE64-DPAPI", json);
        Assert.DoesNotContain(
            SettingsBackup.Import(json, null).Settings.EnumerateObject(),
            p => p.Name.EndsWith("Enc", StringComparison.Ordinal));
    }

    [Fact]
    public void 같은_비밀번호라도_파일마다_다르게_봉인된다()
    {
        // 소금·논스를 매번 새로 뽑는지 — 같으면 두 백업을 비교해 키가 같은지 알 수 있다.
        var secrets = new SettingsBackup.Secrets(DeeplApiKey: "dk-1234");

        Assert.NotEqual(
            SettingsBackup.Export(SettingsJson, secrets, "말"),
            SettingsBackup.Export(SettingsJson, secrets, "말"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"app":"something-else","version":1,"settings":{}}""")]
    [InlineData("그냥 글자")]
    public void 우리_백업이_아니면_거절한다(string json) =>
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, null));

    [Fact]
    public void 더_새_형식은_거절하고_이유를_밝힌다()
    {
        var json = """{"app":"musebase","version":99,"settings":{}}""";

        var error = Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, null));
        Assert.Contains("업데이트", error.Message);
    }

    [Fact]
    public void 잘린_봉인은_손상이라고_말한다()
    {
        // 인증 태그(16바이트)조차 안 되는 길이 — 비밀번호를 대 볼 것도 없이 못 쓴다.
        var json = """
            {"app":"musebase","version":1,"settings":{},
             "secrets":{"kdf":"pbkdf2-sha256","iterations":210000,
                        "salt":"AAAAAAAAAAAAAAAAAAAAAA==","nonce":"AAAAAAAAAAAAAAAA","cipher":"AAAA"}}
            """;

        var error = Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, "말"));
        Assert.Contains("손상", error.Message);
    }

    [Fact]
    public void 모르는_잠금_방식은_거절한다()
    {
        var json = """
            {"app":"musebase","version":1,"settings":{},
             "secrets":{"kdf":"rot13","iterations":1,"salt":"AA==","nonce":"AA==","cipher":"AA=="}}
            """;

        Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, "말"));
    }
}
