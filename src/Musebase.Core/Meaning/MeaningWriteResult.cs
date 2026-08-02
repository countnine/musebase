using System.Net;

namespace Musebase.Core.Meaning;

/// <summary>
/// 의미 생성 한 번의 결과.
///
/// 성공/실패 두 갈래로는 부족해서 <see cref="Retryable"/>을 따로 둔다. 429(쿼타 초과)나
/// 5xx는 **시간이 지나면 저절로 풀리는** 실패인데, 이걸 영구 실패로 저장해 버리면 그 곡은
/// 다시 시도되지 않는다 — 백필은 이미 행이 있는 곡을 건너뛰기 때문이다. 쿼타는 회복되는데
/// 기록만 남아 "이 곡은 의미를 만들 수 없다"가 되는 셈이라, 일시적 실패는 아무것도 남기지
/// 않고 물러나는 편이 옳다.
/// </summary>
/// <param name="Text">생성된 문단. null이면 실패다.</param>
/// <param name="Retryable">시간이 지나면 풀릴 실패인가 — 그렇다면 저장하지 않는다.</param>
public sealed record MeaningWriteResult(string? Text, bool Retryable)
{
    /// <summary>영구 실패(키가 틀렸다, 응답이 비었다 등) — 저장해 두고 넘어간다.</summary>
    public static readonly MeaningWriteResult Failed = new(null, false);

    /// <summary>일시적 실패(쿼타·서버·네트워크·타임아웃) — 저장하지 않는다.</summary>
    public static readonly MeaningWriteResult Transient = new(null, true);

    public static MeaningWriteResult Written(string text) => new(text, false);

    /// <summary>
    /// 429는 쿼타, 5xx는 상대 서버 문제, 402는 잔액 부족 — 셋 다 시간이나 충전으로 풀린다.
    /// 402를 영구 실패로 굳히면 백필 도중 잔액이 떨어졌을 때 남은 곡이 전부 "의미 없음"으로
    /// 박제된다(429에서 고친 것과 같은 병이다).
    /// 나머지 4xx(키 오류·잘못된 요청)는 다시 불러도 같은 답이 오므로 영구 실패다.
    /// </summary>
    public static MeaningWriteResult FromStatus(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired
        || (int)code >= 500
            ? Transient
            : Failed;
}
