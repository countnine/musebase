using Microsoft.Extensions.Logging;

namespace Musebase.Server;

/// <summary>일괄 작업의 종류. 화면 문구와 로그에만 쓰인다.</summary>
public enum BulkJobKind
{
    /// <summary>곡의 의미 일괄 생성.</summary>
    Meaning,

    /// <summary>가사 일괄 번역.</summary>
    Translation,
}

/// <summary>잡의 생애. <see cref="Running"/>이 아니면 끝난 것이고, 마지막 상태는 다음 잡까지 남는다.</summary>
public enum BulkJobStatus
{
    Running,

    /// <summary>대상을 끝까지 돌았다(그 안에 실패가 있을 수 있다).</summary>
    Done,

    /// <summary>사람이 [중지]를 눌렀다.</summary>
    Cancelled,

    /// <summary>스스로 멈췄다 — 쿼타·설정 문제, 상한 도달, 예기치 못한 오류.</summary>
    Stopped,
}

/// <summary>한 곡을 처리한 결과.</summary>
/// <param name="Step">이 곡의 판정. <see cref="BulkStep.Stop"/>이면 잡 전체가 멈춘다.</param>
/// <param name="Detail">사람에게 보여 줄 이유(공급자가 준 문구 등).</param>
/// <param name="Units">이 곡이 실제로 쓴 양(곡 수 잡이면 1, 번역이면 요청한 문자 수).</param>
public sealed record BulkStepResult(BulkStep Step, string? Detail = null, long Units = 0);

/// <summary>한 곡의 판정.</summary>
public enum BulkStep
{
    Ok,

    /// <summary>할 일이 없었다(이미 있음·형식 보존 불가 등) — 실패가 아니다.</summary>
    Skipped,

    /// <summary>만들지 못했지만 곡의 문제다(자료 없음·자료 부족).</summary>
    NoSource,

    /// <summary>이 곡에서 실패했다. 잡은 계속 돈다.</summary>
    Failed,

    /// <summary>
    /// 곡의 문제가 아니라 잡 전체를 더 돌려 봐야 소용없다(쿼타·잔액·키). 남은 곡은 손대지 않는다.
    /// </summary>
    Stop,
}

/// <summary>
/// 일괄 작업이 돌 범위. 검색 화면의 <b>칩과는 다른 축</b>이다 — 칩은 서로 겹치지 않는 단일 선택이라
/// "Spotify 좋아요 ∩ 아직 번역 없음" 같은 조합을 담을 수 없다(칩마다 건수를 박아 두어 합이 전체를
/// 넘으면 안 되기 때문). 그래서 범위(여기)와 "이미 있으면 건너뛰기"를 따로 둔다.
/// </summary>
public static class BulkScope
{
    /// <summary>보관 중인 곡 전부(최근 갱신순).</summary>
    public const string All = "all";

    public const string LovedLastFm = "loved-lastfm";
    public const string LovedSpotify = "loved-spotify";

    /// <summary>어느 한쪽에서라도 좋아요한 곡.</summary>
    public const string LovedAny = "loved-any";

    /// <summary>서버에 없어 기기가 직접 찾은 곡(조회 미스) 중 지금은 서버에 있는 것 — 자주 듣는 곡.</summary>
    public const string Misses = "misses";

    /// <summary><see cref="All"/>과 같은 순서의 앞쪽 — 최근 올라온 곡부터.</summary>
    public const string Recent = "recent";

    public static readonly IReadOnlyList<string> Known =
        [All, LovedLastFm, LovedSpotify, LovedAny, Misses, Recent];

    /// <summary>주소로 들어온 값을 그대로 믿지 않는다(목록 뷰의 화이트리스트 관례와 같다).</summary>
    public static bool IsKnown(string? scope) =>
        scope is not null && Known.Contains(scope, StringComparer.Ordinal);

    public static string Label(string scope) => scope switch
    {
        LovedLastFm => "Last.fm 좋아요",
        LovedSpotify => "Spotify 좋아요",
        LovedAny => "좋아요(Last.fm·Spotify)",
        Misses => "조회 미스 상위",
        Recent => "최근 올라온 곡",
        _ => "전체",
    };
}

/// <summary>일괄 작업의 대상 한 곡.</summary>
public sealed record BulkTarget(string Key, string Title, string Artist)
{
    /// <summary>화면·로그에 쓸 한 줄.</summary>
    public string Label => $"{Artist} - {Title}";
}

/// <summary>진행 화면이 그리는 값 전부. 러너 밖으로는 이 불변 스냅샷만 나간다.</summary>
public sealed record BulkJobState(
    BulkJobKind Kind,
    BulkJobStatus Status,
    string Label,
    int Total,
    int Done,
    int Ok,
    int Skipped,
    int NoSource,
    int Failed,
    long Units,
    long Budget,
    string UnitName,
    string? CurrentLabel,
    /// <summary>지금까지 어느 엔진이 얼마나 채웠는지 같은 한 줄 요약(작업 종류마다 다르다).</summary>
    string? Note,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt)
{
    public bool IsRunning => Status == BulkJobStatus.Running;

    /// <summary>진행률(%). 대상이 0이면 잡이 시작되지 않으므로 0으로 나눌 일은 없다.</summary>
    public int Percent => Total <= 0 ? 100 : (int)Math.Round(Done * 100.0 / Total);

    public TimeSpan Elapsed => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

/// <summary>
/// 서버에서 도는 <b>일괄 작업 한 개</b>. 의미 생성과 가사 번역이 같은 틀을 쓴다.
///
/// <b>왜 HTTP 요청 밖으로 뺐나.</b> 예전 의미 백필은 POST 안에서 동기로 돌아, 한 번에 처리할 곡 수를
/// 프록시·브라우저 타임아웃에 맞춰 50곡쯤으로 묶어 둘 수밖에 없었고 진행 상황을 볼 방법도 중간에
/// 멈출 방법도 없었다. 수천 곡을 돌리려면 요청과 작업을 떼어 놓아야 한다.
///
/// <b>상태는 메모리에만 둔다.</b> 결과물(<c>meanings</c>·<c>lyrics</c>·<c>translation_cache</c> 행)은
/// 이미 내구적이고 잡은 <b>곡 단위로 커밋</b>하므로, 서버가 재시작하면 잃는 것은 최대 한 곡분이다.
/// 잡 기록을 DB에 두면 마이그레이션이 하나 늘고 단일 커넥션에 쓰기만 늘 뿐 얻는 것이 없다.
///
/// <b>한 번에 하나만 돈다.</b> 의미와 번역 둘 다 외부 API와 단일 SQLite 커넥션을 두드려서,
/// 같이 돌면 앱의 가사 조회가 그만큼 밀린다.
/// </summary>
public sealed class BulkJobRunner(ILogger<BulkJobRunner> logger)
{
    private readonly object _lock = new();
    private Job? _job;

    /// <summary>지금(또는 마지막) 잡. 아직 아무것도 돌지 않았으면 null. <b>DB를 건드리지 않는다</b>
    /// — 진행 화면이 2초마다 부르는 자리라 여기서 DB를 치면 그게 곧 조회를 막는 원인이 된다.</summary>
    public BulkJobState? Snapshot()
    {
        lock (_lock) return _job?.ToState();
    }

    /// <summary>
    /// 잡을 시작한다. 이미 도는 것이 있거나 대상이 없으면 <c>false</c>와 사람에게 보여 줄 사유.
    /// </summary>
    /// <param name="work">한 곡을 처리한다. 예외를 던져도 그 곡만 실패로 세고 잡은 계속 돈다.</param>
    /// <param name="delayMs">곡 사이 간격(무료 티어의 분당 한도 대응). 0이면 쉬지 않는다.</param>
    /// <param name="budget">이번 실행의 <paramref name="unitName"/> 상한. 0 이하면 상한 없음.</param>
    /// <param name="estimate">
    /// 곡을 처리하기 <b>전에</b> 드는 양. 상한을 넘을 곡은 아예 시작하지 않는다 —
    /// 처리 중에 끊으면 반쪽짜리 결과가 저장된다. 비우면 곡당 1로 본다.
    /// </param>
    public bool TryStart(
        BulkJobKind kind, string label, IReadOnlyList<BulkTarget> targets,
        Func<BulkTarget, CancellationToken, Task<BulkStepResult>> work,
        int delayMs, long budget, string unitName,
        out string? refusal, Func<BulkTarget, long>? estimate = null, Func<string?>? note = null)
    {
        Job job;
        lock (_lock)
        {
            if (_job is { Status: BulkJobStatus.Running } running)
            {
                refusal = $"이미 실행 중인 작업이 있습니다({running.Label}) — 끝나거나 중지한 뒤 다시 시작하세요.";
                return false;
            }
            if (targets.Count == 0)
            {
                refusal = "대상 곡이 없습니다.";
                return false;
            }

            job = new Job(kind, label, targets.Count, budget, unitName) { Note = note };
            _job = job;
            refusal = null;
        }

        // HttpContext는 응답이 끝나면 버려진다 — 값만 넘기고 요청 객체는 절대 붙잡지 않는다.
        _ = Task.Run(() => RunAsync(job, targets, work, delayMs, estimate));
        return true;
    }

    /// <summary>도는 잡에 중지를 건다. 실제로 멈추는 것은 곡 경계(또는 취소를 받는 작업 안)다.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_job is not { Status: BulkJobStatus.Running } job) return false;
            job.Cts.Cancel();
            return true;
        }
    }

    private async Task RunAsync(
        Job job, IReadOnlyList<BulkTarget> targets,
        Func<BulkTarget, CancellationToken, Task<BulkStepResult>> work,
        int delayMs, Func<BulkTarget, long>? estimate)
    {
        var ct = job.Cts.Token;
        try
        {
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();

                var cost = estimate?.Invoke(target) ?? 1;
                if (job.Budget > 0 && Units(job) + cost > job.Budget)
                {
                    Finish(job, BulkJobStatus.Stopped,
                        $"{job.Budget:N0}{job.UnitName} 상한에 도달해 멈췄습니다 — 남은 곡은 손대지 않았습니다.");
                    return;
                }

                if (Done(job) > 0 && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);

                SetCurrent(job, target.Label);

                BulkStepResult result;
                try
                {
                    result = await work(target, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 한 곡의 사고로 잡 전체를 버리지 않는다 — 세고 넘어간다.
                    logger.LogWarning(ex, "일괄 작업 {Kind}: {Song} 처리 실패", job.Kind, target.Label);
                    result = new BulkStepResult(BulkStep.Failed, ex.Message, cost);
                }

                if (result.Step == BulkStep.Stop)
                {
                    Finish(job, BulkJobStatus.Stopped, result.Detail);
                    return;
                }

                Count(job, result);
            }

            Finish(job, BulkJobStatus.Done, null);
        }
        catch (OperationCanceledException)
        {
            Finish(job, BulkJobStatus.Cancelled, "중지했습니다 — 남은 곡은 손대지 않았습니다.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "일괄 작업 {Kind}가 예기치 못한 오류로 멈췄습니다", job.Kind);
            Finish(job, BulkJobStatus.Stopped, $"예기치 못한 오류로 멈췄습니다 — {ex.Message}");
        }
    }

    private long Units(Job job) { lock (_lock) return job.Units; }

    private int Done(Job job) { lock (_lock) return job.Done; }

    private void SetCurrent(Job job, string label)
    {
        lock (_lock) job.Current = label;
    }

    private void Count(Job job, BulkStepResult result)
    {
        lock (_lock)
        {
            job.Done++;
            job.Units += result.Units;
            switch (result.Step)
            {
                case BulkStep.Ok: job.Ok++; break;
                case BulkStep.Skipped: job.Skipped++; break;
                case BulkStep.NoSource: job.NoSource++; break;
                default: job.Failed++; break;
            }
        }
    }

    private void Finish(Job job, BulkJobStatus status, string? detail)
    {
        BulkJobState state;
        lock (_lock)
        {
            job.Status = status;
            job.Detail = detail;
            job.Current = null;
            job.EndedAt = DateTimeOffset.UtcNow;
            state = job.ToState();
        }

        // 화면은 다음 잡이 시작되면 덮인다 — 무엇이 얼마나 처리됐는지는 로그에 남겨야 나중에 볼 수 있다.
        logger.LogInformation(
            "일괄 작업 {Kind} {Status}: {Done}/{Total}곡 · 성공 {Ok} · 건너뜀 {Skipped} · 자료없음 {NoSource} "
            + "· 실패 {Failed} · {Units}{Unit} · {Elapsed:c} · {Note} · {Detail}",
            state.Kind, state.Status, state.Done, state.Total, state.Ok, state.Skipped, state.NoSource,
            state.Failed, state.Units, state.UnitName, state.Elapsed, state.Note ?? "-",
            state.Detail ?? "(사유 없음)");
    }

    /// <summary>가변 상태는 여기만 산다 — 밖으로는 <see cref="ToState"/>의 불변 사본만 나간다.</summary>
    private sealed class Job(BulkJobKind kind, string label, int total, long budget, string unitName)
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly BulkJobKind Kind = kind;
        public readonly string Label = label;
        public readonly int Total = total;
        public readonly long Budget = budget;
        public readonly string UnitName = unitName;
        public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

        public BulkJobStatus Status = BulkJobStatus.Running;
        public int Done, Ok, Skipped, NoSource, Failed;
        public long Units;
        public string? Current;
        public string? Detail;
        public Func<string?>? Note;
        public DateTimeOffset? EndedAt;

        public BulkJobState ToState() => new(
            Kind, Status, Label, Total, Done, Ok, Skipped, NoSource, Failed,
            Units, Budget, UnitName, Current, Note?.Invoke(), Detail, StartedAt, EndedAt);
    }
}
