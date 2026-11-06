using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ContinuationTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(99, false)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(101, true)]
    public async Task MapErr_MapperThrowsWithContext_KeepsOriginalErrorBeforeContext(int count, bool pending)
    {
        // Given
        var context = Enumerable.Range(0, count).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var original = Outcome<int, string>.Err("original").WithSuppressed([.. context], 2);
        var defect = new IOException("mapper failed");
        var completion = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(completion.Task) : new(original));
        var effect = source.MapErr<int>(_ => throw defect);

        // When
        var running = effect.RunExitAsync(TestContext.Current.CancellationToken);
        if (pending) Assert.False(running.IsCompleted);
        completion.SetResult(original);
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(Math.Min(100, count + 1), die.Suppressed.Count);
        Assert.True(die.Suppressed[0].Case is Failed("original"));
        Assert.Equal(Relation.Sequential, die.Suppressed[0].Relation);
        Assert.Equal(context.Take(99), die.Suppressed.Skip(1));
        Assert.Equal(2 + Math.Max(0, count - 99), die.OmittedSuppressed);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThen_ConcurrentPendingSources_KeepTheirOwnEnvironmentAndToken(bool fail)
    {
        // Given
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var tokens = new CancellationToken[3];
        var effect = Effect<int>.AccessAsync(async (env, token) =>
        {
            tokens[env / 10 - 1] = token;
            await release.Task;
            return env;
        }).AndThen(value =>
        {
            Interlocked.Increment(ref calls);
            return Effect<int>.AccessAsync((env, token) =>
            {
                if (fail) throw new OperationCanceledException(token);
                return new ValueTask<int>(value + env);
            });
        });
        using var canceled = new CancellationTokenSource();
        Assert.Equal(0, calls);

        // When
        var first = effect.RunExitAsync(10, TestContext.Current.CancellationToken);
        var second = effect.RunExitAsync(20, TestContext.Current.CancellationToken);
        var third = effect.RunExitAsync(30, canceled.Token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        Assert.Equal(0, calls);
        canceled.Cancel();
        release.SetResult();
        var exits = await Task.WhenAll(first.AsTask(), second.AsTask(), third.AsTask()).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(3, calls);
        if (fail)
        {
            Assert.True(exits[0].TryGetValue(out Die? firstDie));
            Assert.True(exits[1].TryGetValue(out Die? secondDie));
            Assert.Equal(tokens[0], Assert.IsType<OperationCanceledException>(firstDie.Exception).CancellationToken);
            Assert.Equal(tokens[1], Assert.IsType<OperationCanceledException>(secondDie.Exception).CancellationToken);
            Assert.True(exits[2].TryGetValue(out Interrupted? _));
        }
        else
        {
            Assert.True(exits[0].TryGetValue(out Ok<int> firstValue));
            Assert.True(exits[1].TryGetValue(out Ok<int> secondValue));
            Assert.True(exits[2].TryGetValue(out Ok<int> thirdValue));
            Assert.Equal(20, firstValue.Value);
            Assert.Equal(40, secondValue.Value);
            Assert.Equal(60, thirdValue.Value);
        }
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
