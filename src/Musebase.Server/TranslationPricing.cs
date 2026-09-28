namespace Musebase.Server;

/// <summary>
/// 번역 엔진의 대략적인 단가(100만 자당 USD). <b>서버에만 둔다</b> — 가격은 우리 사정과 무관하게
/// 바뀌는데, 코어나 계약에 넣으면 숫자 하나 고치는 데 클라이언트 릴리스가 묶인다.
///
/// 정확한 청구서가 아니라 <b>실행 전에 자릿수를 알기 위한</b> 값이다("3천 곡이 $5인지 $80인지").
/// 화면에도 추정임을 밝힌다.
/// </summary>
public static class TranslationPricing
{
    /// <summary>100만 자당 USD. 값이 없는 엔진은 환산하지 않는다.</summary>
    private static readonly IReadOnlyDictionary<string, double> Default =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // 무료 한도를 넘긴 뒤의 종량 요금(2026-09 기준). 무료 한도 안이면 실제 청구는 0이다.
            ["deepl"] = 25.0,
            ["google"] = 20.0,

            // 돈이 아니라 한도를 태우는 엔진 — 0으로 두면 "무료"라고 그려진다.
            ["mymemory"] = 0.0,
            ["libretranslate"] = 0.0,

            // openrouter는 일부러 넣지 않는다 — 토큰 과금이라 문자 수로 환산하면 거짓말이 된다.
            // (그 엔진에서는 문자 상한 대신 곡 수 상한이 주된 가드다.)
        };

    /// <summary>
    /// `MUSEBASE_TRANSLATE_PRICE_PER_M="deepl=25,google=20"` 으로 덮어쓴다 —
    /// 가격이 바뀌었을 때 코드를 배포하지 않고 고칠 수 있어야 한다.
    /// </summary>
    public static IReadOnlyDictionary<string, double> FromEnvironment(string? configured = null)
    {
        var raw = configured ?? Environment.GetEnvironmentVariable("MUSEBASE_TRANSLATE_PRICE_PER_M");
        if (string.IsNullOrWhiteSpace(raw)) return Default;

        var prices = new Dictionary<string, double>(Default, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2);
            // 오타로 서버가 죽지 않게 — 못 읽은 항목은 그냥 무시한다(기본값이 남는다).
            if (parts.Length == 2
                && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var price)
                && price >= 0)
                prices[parts[0].Trim()] = price;
        }
        return prices;
    }

    /// <summary>
    /// 화면에 띄울 예상 비용 한 줄. 환산할 수 없는 엔진이면 <c>null</c>이라 화면이 "환산 불가"로 그린다.
    /// </summary>
    public static string? Estimate(string engine, long chars, IReadOnlyDictionary<string, double>? prices = null)
    {
        var table = prices ?? Default;
        if (!table.TryGetValue(engine, out var perMillion)) return null;
        if (perMillion <= 0) return "무료 (한도 소진)";

        var cost = chars / 1_000_000.0 * perMillion;
        return cost < 0.01 ? "약 $0.01 미만" : $"약 ${cost:0.00}";
    }
}
