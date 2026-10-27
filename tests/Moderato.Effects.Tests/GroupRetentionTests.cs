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
