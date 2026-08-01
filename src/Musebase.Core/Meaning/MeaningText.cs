namespace Musebase.Core.Meaning;

/// <summary>제목·아티스트를 견주기 위한 정규화. 소스 구현들이 같은 기준을 쓰게 한다.</summary>
public static class MeaningText
{
    /// <summary>소문자 + 영숫자/한글만 남긴다(괄호·구두점·공백 제거).</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var n = 0;
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToLowerInvariant(c);
        return new string(buffer[..n]);
    }
}
