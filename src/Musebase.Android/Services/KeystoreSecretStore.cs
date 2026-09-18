using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// <see cref="ISecretStore"/>의 Android 구현 — <b>Android Keystore</b>의 AES-256 키로 AES-GCM 암호화한다.
///
/// 키는 Keystore 안에서 만들어져 <b>기기 밖으로 나오지 않는다</b>(앱도 키 바이트를 볼 수 없다).
/// 그래서 암호문이 백업·복사로 다른 기기에 가도 풀 수 없다 — Windows의 DPAPI(CurrentUser)와 같은 성질이다.
/// AndroidX Security(EncryptedSharedPreferences)를 쓰지 않은 것은 패키지 하나를 더 끌어오지 않고
/// 플랫폼 API만으로 같은 일을 할 수 있어서다(그 라이브러리도 내부적으로 Keystore + AES-GCM이다).
///
/// 형식: <c>"v1:" + base64(IV 12바이트 ‖ 암호문+태그)</c>. 풀 수 없으면 null — 기능 강등이지 오류가 아니다.
/// </summary>
public sealed class KeystoreSecretStore : ISecretStore
{
    private const string Provider = "AndroidKeyStore";

    // 이 별칭은 바꾸지 않는다 — 바꾸면 기존 암호문을 풀 키를 잃는다(Windows entropy 문자열과 같은 규칙).
    private const string Alias = "musebase.secrets.v1";
    private const string Prefix = "v1:";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvBytes = 12;
    private const int TagBits = 128;

    private static readonly object KeyLock = new();

    public string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            // IV는 Keystore가 고른다(GCM에서 IV 재사용은 치명적이라 직접 만들지 않는다).
            cipher.Init(CipherMode.EncryptMode, Key());
            var iv = cipher.GetIV()!;
            var sealedBytes = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(plain))!;

            var payload = new byte[iv.Length + sealedBytes.Length];
            Buffer.BlockCopy(iv, 0, payload, 0, iv.Length);
            Buffer.BlockCopy(sealedBytes, 0, payload, iv.Length, sealedBytes.Length);
            return Prefix + Convert.ToBase64String(payload);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("Musebase", $"secret: 암호화 실패 — {ex.GetType().Name}");
            return null;
        }
    }

    public string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText) || !cipherText.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        try
        {
            var payload = Convert.FromBase64String(cipherText[Prefix.Length..]);
            if (payload.Length <= IvBytes) return null;

            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(TagBits, payload, 0, IvBytes));
            var plain = cipher.DoFinal(payload, IvBytes, payload.Length - IvBytes)!;
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // 다른 기기에서 복원된 암호문이거나 키가 지워진 경우 — 그 값은 없는 것으로 본다.
            global::Android.Util.Log.Warn("Musebase", $"secret: 복호 실패 — {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Keystore의 키. 없으면 이 자리에서 만든다(처음 한 번).</summary>
    private static IKey Key()
    {
        lock (KeyLock)
        {
            var store = KeyStore.GetInstance(Provider)!;
            store.Load(null);
            if (store.GetKey(Alias, null) is IKey existing) return existing;

            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
            generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256)
                .Build());
            return generator.GenerateKey()!;
        }
    }
}
