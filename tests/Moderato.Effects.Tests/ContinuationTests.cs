using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ContinuationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Continuation_NextStepDies_KeepsBothStepsSuppressedFailures(bool through, bool pending)
    {
        // Given
        var earlier = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var later = new Suppressed(new Failed("later"), Relation.Parallel);
        var defect = new InvalidOperationException("next step failed");
        var end = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect), [later], omitted: 3);
        var completion = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Ok(5).WithSuppressed([earlier], 2)));
        var next = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(completion.Task) : new(end));
        var composed = through ? source.AndThrough(_ => next) : source.AndThen(_ => next);

        // When
        var running = composed.RunExitAsync(TestContext.Current.CancellationToken);
        if (pending) Assert.False(running.IsCompleted);
        completion.SetResult(end);
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal([earlier, later], die.Suppressed);
        Assert.Equal(5, die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Continuation_PendingNextStep_KeepsTheCorrectValueOrError(bool through, bool fail)
    {
        // Given
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> source = Effect<EmptyEnvironment>.Succeed(5);
        var next = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            var value = await completion.Task;
            return fail ? Result.Err("failed") : Result.Ok(value);
        });
        var composed = through ? source.AndThrough(_ => next) : source.AndThen(_ => next);

        // When
        var running = composed.RunAsync(TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted);
        completion.SetResult(42);
        var result = await running;

        // Then
        Assert.Equal<Result<int, string>>(fail ? Result.Err("failed") : Result.Ok(through ? 5 : 42), result);
    }

    [Fact]
    public async Task AccessAsync_ConcurrentRuns_KeepTheirOwnEnvironmentAndCancellation()
    {
        // Given
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<int>.AccessAsync(async (env, token) =>
        {
            await release.Task.WaitAsync(token);
            return env;
        });
        using var canceled = new CancellationTokenSource();

        // When
        var first = effect.RunAsync(10, TestContext.Current.CancellationToken);
        var second = effect.RunAsync(20, TestContext.Current.CancellationToken);
        var third = effect.RunAsync(30, canceled.Token);
        canceled.Cancel();
        release.SetResult();

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(10), await first);
        Assert.Equal<Result<int, Never>>(Result.Ok(20), await second);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await third);
    }
}
