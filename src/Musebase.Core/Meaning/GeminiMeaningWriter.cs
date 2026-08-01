using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// Google Gemini Developer API(<c>generativelanguage.googleapis.com</c>)로 의미를 쓴다.
///
/// **Vertex AI가 아니라 Developer API를 쓰는 이유**: 인증이 API 키 한 줄이라
/// <see cref="Translation.GoogleTranslateTranslator"/>와 완전히 같은 패턴이고(서비스 계정·ADC 불필요),
/// 무료 티어가 있어 보유 곡 전체를 0원에 채울 수 있다. IAM·데이터 레지던시 같은 거버넌스가
/// 필요해지면 그때 Vertex로 옮기면 된다.
///
/// 키는 <see href="https://aistudio.google.com/apikey"/>에서 만들고, 기존 GCP 프로젝트에
/// 결제를 연결하면 무료 크레딧이 그대로 적용된다.
/// </summary>
public sealed class GeminiMeaningWriter : IMeaningWriter
{
    /// <summary>무료 티어 한도가 가장 넉넉한 모델. 요약 작업엔 충분하다.</summary>
    public const string DefaultModel = "gemini-2.5-flash-lite";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    public GeminiMeaningWriter(string apiKey, string? model = null, HttpClient? http = null, int timeoutMs = 30_000)
    {
        _apiKey = apiKey.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 120_000));
    }

    public string EngineId => "gemini";
    public string Model { get; }

    public async Task<string?> WriteAsync(
        string title, string artist, IReadOnlyList<MeaningSource> sources,
        string targetLang, CancellationToken ct = default)
    {
        if (_apiKey.Length == 0 || sources.Count == 0) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var prompt = MeaningPrompt.Build(title, artist, sources, targetLang);
            var payload = new GeminiRequest
            {
                Contents = [new GeminiContent { Parts = [new GeminiPart { Text = prompt }] }],
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{Model}:generateContent")
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Add("x-goog-api-key", _apiKey);

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadFromJsonAsync<GeminiResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Candidates?
                .FirstOrDefault()?.Content?.Parts?
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }
        catch (Exception)
        {
            return null; // 의미는 부가 기능 — 실패해도 가사에 영향이 없어야 한다
        }
    }

    // ---- 요청/응답 모델(필요한 필드만) ----

    private sealed record GeminiRequest
    {
        public GeminiContent[] Contents { get; init; } = [];
    }

    private sealed record GeminiContent
    {
        public GeminiPart[] Parts { get; init; } = [];
    }

    private sealed record GeminiPart
    {
        public string? Text { get; init; }
    }

    private sealed record GeminiResponse(GeminiCandidate[]? Candidates);
    private sealed record GeminiCandidate(GeminiContentOut? Content);
    private sealed record GeminiContentOut(GeminiPart[]? Parts);
}
