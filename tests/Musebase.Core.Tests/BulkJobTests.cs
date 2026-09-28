using Microsoft.Extensions.Logging.Abstractions;
using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 서버의 일괄 작업 러너. 실제 생성·번역은 델리게이트라 여기서는 <b>루프의 규칙</b>만 본다 —
/// 동시 실행 금지, 중단, 취소, 상한. 이 규칙이 깨지면 쿼타·비용이 그대로 샌다.
/// </summary>
public class BulkJobTests
{
    private static BulkJobRunner NewRunner() => new(NullLogger<BulkJobRunner>.Instance);

    private static IReadOnlyList<BulkTarget> Targets(params string[] keys) =>
        keys.Select(k => new BulkTarget(k, $"곡 {k}", "아티스트")).ToList();

    /// <summary>잡은 백그라운드라 끝날 때까지 기다린다(5초면 이 테스트들엔 넉넉하다).</summary>
    private static async Task<BulkJobState> WaitDoneAsync(BulkJobRunner runner)
    {
        for (var i = 0; i < 500; i++)
        {
            if (runner.Snapshot() is { IsRunning: false } done) return done;
            await Task.Delay(10);
        }
        throw new TimeoutException("잡이 끝나지 않았습니다.");
    }

    [Fact]
    public async Task 이미_실행_중이면_두_번째_잡을_거절한다()
    {
        var runner = NewRunner();
        var gate = new TaskCompletionSource();

        Assert.True(runner.TryStart(
            BulkJobKind.Meaning, "첫 잡", Targets("a"),
            async (_, _) => { await gate.Task; return new BulkStepResult(BulkStep.Ok, Units: 1); },
            delayMs: 0, budget: 0, unitName: "곡", out _));

        var second = runner.TryStart(
            BulkJobKind.Translation, "둘째 잡", Targets("b"),
            (_, _) => Task.FromResult(new BulkStepResult(BulkStep.Ok, Units: 1)),
            delayMs: 0, budget: 0, unitName: "자", out var refusal);

        Assert.False(second);
        Assert.Contains("실행 중", refusal);

        gate.SetResult();
        var state = await WaitDoneAsync(runner);
        Assert.Equal(BulkJobStatus.Done, state.Status);
    }

    [Fact]
    public void 대상이_없으면_시작하지_않는다()
    {
        var runner = NewRunner();

        var started = runner.TryStart(
            BulkJobKind.Meaning, "빈 잡", [],
            (_, _) => Task.FromResult(new BulkStepResult(BulkStep.Ok)),
            delayMs: 0, budget: 0, unitName: "곡", out var refusal);

        Assert.False(started);
        Assert.Contains("대상 곡이 없습니다", refusal);
        Assert.Null(runner.Snapshot());
    }

    [Fact]
    public async Task Stop을_받으면_남은_곡은_손대지_않는다()
    {
        var runner = NewRunner();
        var seen = new List<string>();

        runner.TryStart(
            BulkJobKind.Meaning, "중단 잡", Targets("a", "b", "c"),
            (target, _) =>
            {
                seen.Add(target.Key);
                return Task.FromResult(target.Key == "b"
                    ? new BulkStepResult(BulkStep.Stop, "쿼타가 찼습니다")
                    : new BulkStepResult(BulkStep.Ok, Units: 1));
            },
            delayMs: 0, budget: 0, unitName: "곡", out _);

        var state = await WaitDoneAsync(runner);

        Assert.Equal(["a", "b"], seen);               // c는 아예 시작하지 않는다
        Assert.Equal(BulkJobStatus.Stopped, state.Status);
        Assert.Equal("쿼타가 찼습니다", state.Detail);
        Assert.Equal(1, state.Done);                   // 멈춘 곡은 처리로 세지 않는다
        Assert.Equal(1, state.Ok);
    }

    [Fact]
    public async Task 중지하면_곡_경계에서_멈춘다()
    {
        var runner = NewRunner();
        var seen = new List<string>();

        runner.TryStart(
            BulkJobKind.Meaning, "취소 잡", Targets("a", "b", "c"),
            (target, _) =>
            {
                seen.Add(target.Key);
                // 처리 중에 사람이 [중지]를 누른 상황 — 이 곡은 끝까지 하고 다음 곡부터 멈춰야 한다.
                if (target.Key == "a") runner.Cancel();
                return Task.FromResult(new BulkStepResult(BulkStep.Ok, Units: 1));
            },
            delayMs: 0, budget: 0, unitName: "곡", out _);

        var state = await WaitDoneAsync(runner);

        Assert.Equal(["a"], seen);
        Assert.Equal(BulkJobStatus.Cancelled, state.Status);
        Assert.Equal(1, state.Done);
    }

    [Fact]
    public async Task 상한을_넘길_곡은_아예_시작하지_않는다()
    {
        var runner = NewRunner();
        var seen = new List<string>();

        runner.TryStart(
            BulkJobKind.Translation, "상한 잡", Targets("a", "b", "c"),
            (target, _) =>
            {
                seen.Add(target.Key);
                return Task.FromResult(new BulkStepResult(BulkStep.Ok, Units: 100));
            },
            delayMs: 0, budget: 150, unitName: "자", out _,
            estimate: _ => 100);

        var state = await WaitDoneAsync(runner);

        // 둘째 곡은 100자를 더 쓰면 150자를 넘으므로 처리를 시작조차 하지 않는다
        // (도중에 끊으면 반쯤 번역된 가사가 저장된다).
        Assert.Equal(["a"], seen);
        Assert.Equal(BulkJobStatus.Stopped, state.Status);
        Assert.Equal(100, state.Units);
        Assert.Contains("상한", state.Detail);
    }

    [Fact]
    public async Task 한_곡이_예외를_던져도_잡은_계속_돈다()
    {
        var runner = NewRunner();

        runner.TryStart(
            BulkJobKind.Meaning, "사고 잡", Targets("a", "b"),
            (target, _) => target.Key == "a"
                ? throw new InvalidOperationException("터졌다")
                : Task.FromResult(new BulkStepResult(BulkStep.Ok, Units: 1)),
            delayMs: 0, budget: 0, unitName: "곡", out _);

        var state = await WaitDoneAsync(runner);

        Assert.Equal(BulkJobStatus.Done, state.Status);
        Assert.Equal(2, state.Done);
        Assert.Equal(1, state.Failed);
        Assert.Equal(1, state.Ok);
    }

    [Fact]
    public async Task 끝난_잡의_결과는_화면에_남는다()
    {
        var runner = NewRunner();

        runner.TryStart(
            BulkJobKind.Meaning, "요약 잡", Targets("a", "b", "c"),
            (target, _) => Task.FromResult(target.Key switch
            {
                "a" => new BulkStepResult(BulkStep.Ok, Units: 1),
                "b" => new BulkStepResult(BulkStep.NoSource, Units: 1),
                _ => new BulkStepResult(BulkStep.Skipped, Units: 1),
            }),
            delayMs: 0, budget: 0, unitName: "곡", out _);

        var state = await WaitDoneAsync(runner);

        Assert.Equal(3, state.Done);
        Assert.Equal(1, state.Ok);
        Assert.Equal(1, state.NoSource);
        Assert.Equal(1, state.Skipped);
        Assert.Equal(100, state.Percent);
        Assert.NotNull(state.EndedAt);
        Assert.Null(state.CurrentLabel);   // 끝난 잡은 "지금 이 곡"이 없다
    }
}
