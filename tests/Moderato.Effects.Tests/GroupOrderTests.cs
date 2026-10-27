using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class GroupOrderTests
{
    [Fact]
    public async Task All_LaterInputFinishesFirst_PreservesCompletionContextAndInputValues()
    {
        // Given
        var allowFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("projection failed");
        var context = Enumerable.Range(0, 3).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        Effect<int, string, EmptyEnvironment>[] children =
        [
            new(async (_, _) =>
            {
                await allowFirst.Task;
                return Outcome<int, string>.Ok(0).WithSuppressed([context[0]], 0);
            }),
            new((_, _) => new(Outcome<int, string>.Ok(1).WithSuppressed([context[1]], 0))),
            new((_, _) =>
            {
                allowFirst.SetResult();
                return new(Outcome<int, string>.Ok(2).WithSuppressed([context[2]], 0));
            }),
        ];
        int[]? values = null;

        // When
        var exit = await Effect.All(children, Concurrency.Parallel.WithMaxDegree(2))
            .Map<int>(result => { values = result; throw failure; })
            .RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(values);
        Assert.Equal([0, 1, 2], values);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(3, die.Suppressed.Count);
        Assert.Same(context[1], die.Suppressed[0]);
        Assert.All(context, item => Assert.Contains(item, die.Suppressed));
    }

    [Fact]
    public async Task All_StopsBeforeLaterChildren_CollectsOnlyCompletedContext()
    {
        // Given
        var failure = new IOException("primary");
        var context = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var unstartedCalls = 0;
        Effect<int, string, EmptyEnvironment>[] children =
        [
            new((_, _) => new(Outcome<int, string>.Ok(1).WithSuppressed([context], 2))),
            Effect<EmptyEnvironment>.Sync<int>(() => throw failure),
            Effect<EmptyEnvironment>.Sync(() => ++unstartedCalls),
        ];

        // When
        var exit = await Effect.All(children).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Same(context, Assert.Single(die.Suppressed));
        Assert.Equal(2, die.OmittedSuppressed);
        Assert.Equal(0, unstartedCalls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(256, false)]
    [InlineData(257, false)]
    [InlineData(1024, false)]
    [InlineData(8, true)]
    [InlineData(257, true)]
    public async Task All_ChildrenCarryFailures_KeepsInputValuesAndCompletionContext(int count, bool parallel)
    {
        // Given
        var failure = new IOException("projection failed");
        var context = Enumerable.Range(0, count).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        Effect<int, string, EmptyEnvironment>[] children = [.. Enumerable.Range(0, count).Select(i =>
            new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Ok(i).WithSuppressed([context[i]], 1))))];
        int[]? values = null;
        var concurrency = parallel ? Concurrency.Parallel.WithMaxDegree(4) : Concurrency.Sequential;

        // When
        var exit = await Effect.All(children, concurrency).Map<int>(result => { values = result; throw failure; })
            .RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(Enumerable.Range(0, count), values);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(Math.Min(100, count), die.Suppressed.Count);
        Assert.Equal(count + Math.Max(0, count - 100), die.OmittedSuppressed);
        Assert.Equal(die.Suppressed.Count, die.Suppressed.Distinct().Count());
        for (var i = 0; i < die.Suppressed.Count; i++)
        {
            var index = Assert.IsType<int>(Assert.IsType<Failed>(die.Suppressed[i].Case.Value).Error);
            Assert.InRange(index, 0, count - 1);
            Assert.Same(context[index], die.Suppressed[i]);
            if (!parallel) Assert.Equal(i, index);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(257)]
    public async Task AllErrors_ErrChildrenCarryContext_KeepsErrorsInInputOrderWithoutDuplicatingThem(int count)
    {
        // Given
        var failure = new IOException("projection failed");
        var context = Enumerable.Range(0, count).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        Effect<int, string, EmptyEnvironment>[] children = [.. Enumerable.Range(0, count).Select(i =>
            new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Err(i.ToString()).WithSuppressed([context[i]], 1))))];

        // When
        var exit = await Effect.AllErrors(children).MapErr<string>(_ => throw failure).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        var errors = Assert.IsType<string[]>(Assert.IsType<Failed>(die.Suppressed[0].Case.Value).Error);
        Assert.Equal(Enumerable.Range(0, count).Select(i => i.ToString()), errors);
        Assert.Equal(Math.Min(100, count + 1), die.Suppressed.Count);
        Assert.Equal(count + Math.Max(0, count - 99), die.OmittedSuppressed);
        for (var i = 1; i < die.Suppressed.Count; i++) Assert.Same(context[i - 1], die.Suppressed[i]);
    }
}
