using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class GroupRetentionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task AllErrors_ChildrenAndPrimaryHaveContext_KeepsPrimaryPrefixAndCountsEveryOmission(int primaryCount)
    {
        // Given
        var failure = new IOException("primary");
        var primaryContext = Enumerable.Range(0, primaryCount).Select(i => new Suppressed(new Failed($"primary {i}"), Relation.Sequential)).ToArray();
        var childContext = Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 60)
            .Select(j => new Suppressed(new Failed($"child {i}/{j}"), Relation.Consequence)).ToArray()).ToArray();
        Effect<int, string, EmptyEnvironment>[] children = new Effect<int, string, EmptyEnvironment>[4];
        for (var i = 0; i < 3; i++)
        {
            var outcome = Outcome<int, string>.Err($"error {i}").WithSuppressed([.. childContext[i]], 2);
            children[i] = new((_, _) => new(outcome));
        }

        var primary = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(failure), [.. primaryContext], omitted: 5);
        children[^1] = new((_, _) => new(primary));
        Suppressed[] expected = [.. primaryContext, .. Enumerable.Range(0, 3).SelectMany(i =>
            new[] { new Suppressed(new Failed($"error {i}"), Relation.Parallel) }.Concat(childContext[i]))];

        // When
        var exit = await Effect.AllErrors(children).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(11 + expected.Length - 100, die.OmittedSuppressed);
        for (var i = 0; i < primaryCount; i++) Assert.Same(primaryContext[i], die.Suppressed[i]);
    }

    [Fact]
    public async Task AllErrors_ContextIsFull_StillProjectsEveryErrorInOrder()
    {
        // Given
        var reads = new List<int>();
        var failure = new IOException("primary");
        Effect<int, ObservedError, EmptyEnvironment>[] children = [.. Enumerable.Range(0, 105).Select(i =>
            (Effect<int, ObservedError, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail(new ObservedError(i, reads, i == 102))),
            Effect<EmptyEnvironment>.Sync<int>(() => throw failure)];

        // When
        var exit = await Effect.AllErrors(children).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(Enumerable.Range(0, 105), reads);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(5, die.OmittedSuppressed);
        for (var i = 0; i < die.Suppressed.Count; i++) Assert.Equal(i, Assert.IsType<Failed>(die.Suppressed[i].Case.Value).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task All_DefectsFollowRetainedContext_PreservesPrefixAndOmissions(int contextCount)
    {
        // Given
        var context = Enumerable.Range(0, contextCount).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var failures = new[] { new IOException("first"), new IOException("second") };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Effect<int, string, EmptyEnvironment> prefix = new((_, _) => new(Outcome<int, string>.Ok(42).WithSuppressed([.. context], 4)));
        var children = new[] { prefix }.Concat(failures.Select(failure => new Effect<int, string, EmptyEnvironment>(async (_, _) =>
        {
            if (Interlocked.Increment(ref started) == 2) ready.SetResult();
            await ready.Task.ConfigureAwait(false);
            return Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(failure), omitted: 2);
        }))).ToArray();

        // When
        var exit = await Effect.All(children, Concurrency.Parallel.WithMaxDegree(2)).RunExitAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Contains(die.Exception, failures);
        Assert.Equal(context, die.Suppressed.Take(contextCount));
        Assert.Equal(Math.Min(100, contextCount + 1), die.Suppressed.Count);
        Assert.Equal(8 + Math.Max(0, contextCount + 1 - 100), die.OmittedSuppressed);
        if (contextCount < 100)
        {
            var other = Assert.IsType<Die>(die.Suppressed[^1].Case.Value);
            Assert.Contains(other.Exception, failures);
            Assert.NotSame(die.Exception, other.Exception);
            Assert.Equal(Relation.Parallel, die.Suppressed[^1].Relation);
        }
    }

    private sealed class ObservedError(int index, List<int> reads, bool throws) : System.Runtime.CompilerServices.IUnion
    {
        public object? Value
        {
            get
            {
                reads.Add(index);
                if (throws) throw new InvalidOperationException("error getter");
                return index;
            }
        }
    }
}
