using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class LinqContinuationTests
{
    public static TheoryData<int, bool, bool> CompletionModes
    {
        get
        {
            var cases = new TheoryData<int, bool, bool>();
            for (var overload = 0; overload < 4; overload++)
            {
                foreach (var sourcePending in new[] { false, true })
                {
                    foreach (var nextPending in new[] { false, true }) cases.Add(overload, sourcePending, nextPending);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(CompletionModes))]
    public async Task SelectMany_AllOverloads_ProjectTheCorrectValues(int overload, bool sourcePending, bool nextPending)
    {
        // Given
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!sourcePending) first.SetResult(20);
        if (!nextPending) second.SetResult(22);
        var source = Effect<EmptyEnvironment>.Async(_ => new ValueTask<int>(first.Task));
        var next = Effect<EmptyEnvironment>.Async(_ => new ValueTask<int>(second.Task));
        var effect = Query(overload, source, next, static (x, y) => x + y);

        // When
        var running = effect.RunAsync(TestContext.Current.CancellationToken);
        if (sourcePending)
        {
            Assert.False(running.IsCompleted);
            first.SetResult(20);
        }

        if (nextPending)
        {
            Assert.False(running.IsCompleted);
            second.SetResult(22);
        }

        // Then
        Assert.Equal<Result<int, string>>(Result.Ok(42), await running);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task SelectMany_ProjectionThrows_KeepsBothStepsSuppressedFailures(int overload, bool nextPending)
    {
        // Given
        var earlier = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var later = new Suppressed(new Failed("later"), Relation.Parallel);
        var source = new Effect<int, Never, EmptyEnvironment>((_, _) => new(Outcome<int, Never>.Ok(20).WithSuppressed([earlier], 2)));
        var completion = new TaskCompletionSource<Outcome<int, Never>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var end = Outcome<int, Never>.Ok(22).WithSuppressed([later], 3);
        var next = new Effect<int, Never, EmptyEnvironment>((_, _) => nextPending ? new(completion.Task) : new(end));
        var defect = new InvalidOperationException("projection failed");
        var effect = Query(overload, source, next, (_, _) => throw defect);

        // When
        var running = effect.RunExitAsync(TestContext.Current.CancellationToken);
        if (nextPending) Assert.False(running.IsCompleted);
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
    public async Task SelectMany_NextStepFails_DoesNotCallTheProjection(bool pending)
    {
        // Given
        var completion = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var end = Outcome<int, string>.Err("failed");
        Effect<int, string, EmptyEnvironment> source = Effect<EmptyEnvironment>.Succeed(20);
        var next = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(completion.Task) : new(end));
        Func<int, int, int> project = static (_, _) => throw new InvalidOperationException("must not project");
        var effect = source.SelectMany(_ => next, project);

        // When
        var running = effect.RunAsync(TestContext.Current.CancellationToken);
        completion.SetResult(end);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("failed"), await running);
    }

    [Fact]
    public async Task SelectMany_SelectorThrows_KeepsTheSourceFailureContext()
    {
        // Given
        var earlier = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Ok(20).WithSuppressed([earlier], 2)));
        var defect = new InvalidOperationException("selector failed");
        var effect = source.SelectMany((int _) => Throw(), static (int x, int y) => x + y);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal([earlier], die.Suppressed);
        Assert.Equal(2, die.OmittedSuppressed);

        Effect<int, string, EmptyEnvironment> Throw() => throw defect;
    }

    [Fact]
    public async Task SelectMany_CallerCancelsInProjection_RemainsInterrupted()
    {
        // Given
        using var caller = new CancellationTokenSource();
        var source = Effect<EmptyEnvironment>.Succeed(20);
        var next = Effect<EmptyEnvironment>.Succeed(22);
        Func<int, int, int> project = (x, y) =>
        {
            caller.Cancel();
            throw new OperationCanceledException();
        };
        var effect = source.SelectMany(_ => next, project);

        // When
        var exit = await effect.RunExitAsync(caller.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SelectMany_ConstructingQueries_DoesNotRunStepsOrProjection(int overload)
    {
        // Given
        var sourceRuns = 0;
        var nextRuns = 0;
        var projections = 0;
        var source = Effect<EmptyEnvironment>.Sync(() => { sourceRuns++; return 20; });
        var next = Effect<EmptyEnvironment>.Sync(() => { nextRuns++; return 22; });
        Effect<int, string, EmptyEnvironment> typedSource = source;
        Effect<int, string, EmptyEnvironment> typedNext = next;
        int Project(int a, int b) { projections++; return a + b; }
        Effect<int, string, EmptyEnvironment> query = default;

        // When
        for (var i = 0; i < 8; i++)
        {
            query = overload switch
            {
                0 => from a in typedSource from b in typedNext select Project(a, b),
                1 => from a in typedSource from b in next select Project(a, b),
                2 => from a in source from b in typedNext select Project(a, b),
                _ => from a in source from b in next select Project(a, b),
            };
        }
        var beforeRun = (sourceRuns, nextRuns, projections);
        var first = await query.RunAsync(TestContext.Current.CancellationToken);
        var second = await query.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal((0, 0, 0), beforeRun);
        Assert.Equal((2, 2, 2), (sourceRuns, nextRuns, projections));
        Assert.Equal<Result<int, string>>(Result.Ok(42), first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(0, false, 99)]
    [InlineData(0, true, 1)]
    [InlineData(0, true, 99)]
    [InlineData(1, false, 1)]
    [InlineData(1, false, 99)]
    [InlineData(1, true, 1)]
    [InlineData(1, true, 99)]
    [InlineData(2, false, 1)]
    [InlineData(2, false, 99)]
    [InlineData(2, true, 1)]
    [InlineData(2, true, 99)]
    [InlineData(3, false, 1)]
    [InlineData(3, false, 99)]
    [InlineData(3, true, 1)]
    [InlineData(3, true, 99)]
    public async Task SelectMany_SuccessThenDefect_PreservesBothStepsContext(int overload, bool pending, int firstCount)
    {
        // Given
        var earlier = Enumerable.Range(0, firstCount).Select(i => new Suppressed(new Failed($"earlier {i}"), Relation.Sequential)).ToArray();
        var later = Enumerable.Range(0, 2).Select(i => new Suppressed(new Failed($"later {i}"), Relation.Parallel)).ToArray();
        var first = Outcome<int, Never>.Ok(20).WithSuppressed([.. earlier], 2);
        var last = Outcome<int, Never>.Ok(22).WithSuppressed([.. later], 3);
        var completion = new TaskCompletionSource<Outcome<int, Never>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, Never, EmptyEnvironment>((_, _) => new(first));
        var next = new Effect<int, Never, EmptyEnvironment>((_, _) => pending ? new(completion.Task) : new(last));
        var query = Query(overload, source, next, static (a, b) => a + b);
        var failure = new IOException("after projection");
        var seen = 0;

        // When
        var running = query.AndThen(value => Effect<EmptyEnvironment>.Sync<int>(() => { seen = value; throw failure; }))
            .RunExitAsync(TestContext.Current.CancellationToken);
        if (pending) Assert.False(running.IsCompleted);
        completion.SetResult(last);
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(42, seen);
        Assert.Equal(earlier.Concat(later).Take(100), die.Suppressed);
        Assert.Equal(5 + Math.Max(0, firstCount + 2 - 100), die.OmittedSuppressed);
    }

    private static Effect<int, string, EmptyEnvironment> Query(int overload,
        Effect<int, Never, EmptyEnvironment> source, Effect<int, Never, EmptyEnvironment> next, Func<int, int, int> project)
    {
        Effect<int, string, EmptyEnvironment> typedSource = source;
        Effect<int, string, EmptyEnvironment> typedNext = next;
        return overload switch
        {
            0 => typedSource.SelectMany(_ => typedNext, project),
            1 => typedSource.SelectMany(_ => next, project),
            2 => source.SelectMany(_ => typedNext, project),
            3 => source.SelectMany(_ => next, project),
            _ => throw new ArgumentOutOfRangeException(nameof(overload)),
        };
    }
}
