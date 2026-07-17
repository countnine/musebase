using System.Net;
using System.Net.Http;

namespace Musebase.Core.Translation;

/// <summary>번역기 실패 정보(로깅·상태 힌트용). 개인정보·가사 본문은 담지 않는다.</summary>
public sealed record TranslatorFailure(string EngineId, int? HttpStatus, TranslatorFailureKind Kind);

/// <summary>실패 분류 — App이 사용자 안내 문구로 현지화한다.</summary>
public enum TranslatorFailureKind
{
    Quota,    // 할당량 초과 (DeepL 456)
    Auth,     // 인증 실패/거부 (401/403)
    RateLimit,// 요청 과다 (429)
    Server,   // 서버 오류 (5xx)
    Network,  // 연결 실패 등
    Other,
}

/// <summary>
/// 여러 번역기를 순서대로 시도하는 폴백 체인(ADR-0002).
/// 앞 번역기가 실패(예: DeepL 할당량 456)하면 남은 항목만 다음 번역기로 채운다.
/// 개별 번역기 실패는 <paramref name="onFailure"/>로 보고하되 예외로 전파하지 않는다
/// (취소는 예외).  단일 번역기를 감싸도 실패 보고 경로가 생긴다.
/// </summary>
public sealed class CompositeTranslator : ITranslator
{
    private readonly IReadOnlyList<(string EngineId, ITranslator Translator)> _chain;
    private readonly Action<TranslatorFailure>? _onFailure;

    public CompositeTranslator(
        IReadOnlyList<(string EngineId, ITranslator Translator)> chain,
        Action<TranslatorFailure>? onFailure = null)
    {
        _chain = chain;
        _onFailure = onFailure;
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var results = new string?[texts.Count];
        var pendingIndices = Enumerable.Range(0, texts.Count).ToList();

        foreach (var (engineId, translator) in _chain)
        {
            if (pendingIndices.Count == 0) break;

            var pendingTexts = pendingIndices.Select(i => texts[i]).ToList();
            IReadOnlyList<string?> partial;
            try
            {
                partial = await translator.TranslateAsync(pendingTexts, targetLang, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _onFailure?.Invoke(new TranslatorFailure(engineId, HttpStatusOf(ex), Classify(ex)));
                continue; // 다음 번역기로 폴백
            }

            var stillPending = new List<int>();
            for (var i = 0; i < pendingIndices.Count; i++)
            {
                var value = i < partial.Count ? partial[i] : null;
                if (value is { Length: > 0 }) results[pendingIndices[i]] = value;
                else stillPending.Add(pendingIndices[i]);
            }
            pendingIndices = stillPending;
        }

        return results;
    }

    private static int? HttpStatusOf(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code } ? (int)code : null;

    private static TranslatorFailureKind Classify(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            var status = (int?)hre.StatusCode;
            return status switch
            {
                456 => TranslatorFailureKind.Quota,     // DeepL: 할당량 초과
                401 or 403 => TranslatorFailureKind.Auth,
                429 => TranslatorFailureKind.RateLimit,
                >= 500 and <= 599 => TranslatorFailureKind.Server,
                null => TranslatorFailureKind.Network,   // 연결 실패(상태 코드 없음)
                _ => TranslatorFailureKind.Other,
            };
        }
        return TranslatorFailureKind.Network;
    }
}
