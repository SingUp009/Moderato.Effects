using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ContinuationTests
{
    [Fact]
    public async Task MapErr_MapperThrowsWithSaturatedContext_KeepsOriginalErrorBeforeContext()
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var original = Outcome<int, string>.Err("original").WithSuppressed([.. context], 2);
        var defect = new IOException("mapper failed");
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(original));
        var effect = source.MapErr<int>(_ => throw defect);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.True(die.Suppressed[0].Case is Failed("original"));
        Assert.Equal(Relation.Sequential, die.Suppressed[0].Relation);
        Assert.Equal(context.Take(99), die.Suppressed.Skip(1));
        Assert.Equal(3, die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(50, 80)]
    [InlineData(99, 2)]
    [InlineData(99, 100)]
    [InlineData(100, 0)]
    [InlineData(100, 2)]
    [InlineData(101, 2)]
    [InlineData(110, 120)]
    public async Task AndThen_CombinesFailureContext_PreservesPrefixAndOmittedCount(int earlierCount, int laterCount)
    {
        // Given
        var earlier = Enumerable.Range(0, earlierCount).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var later = Enumerable.Range(earlierCount, laterCount).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var defect = new IOException("next step failed");
        var first = Outcome<int, string>.Ok(1).WithSuppressed([.. earlier], 2);
        var last = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect), [.. later], omitted: 3);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(first));
        var next = new Effect<int, string, EmptyEnvironment>((_, _) => new(last));
        var effect = source.AndThen(_ => next);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(earlier.Concat(later).Take(100), die.Suppressed);
        Assert.Equal(5 + Math.Max(0, earlierCount + laterCount - 100), die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThen_NextStepDies_KeepsBothStepsSuppressedFailures(bool pending)
    {
        // Given
        var earlier = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var later = new Suppressed(new Failed("later"), Relation.Parallel);
        var defect = new InvalidOperationException("next step failed");
        var end = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect), [later], omitted: 3);
        var completion = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Ok(5).WithSuppressed([earlier], 2)));
        var next = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(completion.Task) : new(end));
        var composed = source.AndThen(_ => next);

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThrough_NextStepDies_KeepsBothStepsSuppressedFailures(bool pending)
    {
        // Given
        var earlier = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var later = new Suppressed(new Failed("later"), Relation.Parallel);
        var defect = new InvalidOperationException("next step failed");
        var end = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect), [later], omitted: 3);
        var completion = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Ok(5).WithSuppressed([earlier], 2)));
        var next = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(completion.Task) : new(end));
        var composed = source.AndThrough(_ => next);

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThen_PendingNextStep_KeepsTheNextValueOrError(bool fail)
    {
        // Given
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> source = Effect<EmptyEnvironment>.Succeed(5);
        var next = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            var value = await completion.Task;
            return fail ? Result.Err("failed") : Result.Ok(value);
        });
        var composed = source.AndThen(_ => next);

        // When
        var running = composed.RunAsync(TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted);
        completion.SetResult(42);
        var result = await running;

        // Then
        Assert.Equal<Result<int, string>>(fail ? Result.Err("failed") : Result.Ok(42), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThrough_PendingNextStep_KeepsTheSourceValueOrTheNextError(bool fail)
    {
        // Given
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> source = Effect<EmptyEnvironment>.Succeed(5);
        var next = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            var value = await completion.Task;
            return fail ? Result.Err("failed") : Result.Ok(value);
        });
        var composed = source.AndThrough(_ => next);

        // When
        var running = composed.RunAsync(TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted);
        completion.SetResult(42);
        var result = await running;

        // Then
        Assert.Equal<Result<int, string>>(fail ? Result.Err("failed") : Result.Ok(5), result);
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
