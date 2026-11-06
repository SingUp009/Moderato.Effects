using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class GroupCompositionContextTests
{
    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(1, 3, false)]
    [InlineData(99, 3, false)]
    [InlineData(100, 3, false)]
    [InlineData(100, 100, false)]
    [InlineData(1, 3, true)]
    [InlineData(99, 3, true)]
    [InlineData(100, 100, true)]
    public async Task All_LaterChildFails_ContextMatchesSequentialComposition(int earlierCount, int laterCount, bool parallel)
    {
        // Given
        var failure = new IOException("later child");
        var earlier = Enumerable.Range(0, earlierCount).Select(i => new Suppressed(new Failed($"earlier {i}"), Relation.Sequential)).ToArray();
        var later = Enumerable.Range(0, laterCount).Select(i => new Suppressed(new Failed($"later {i}"), Relation.Consequence)).ToArray();
        var firstOutcome = Outcome<int, string>.Ok(1).WithSuppressed([.. earlier], 2);
        var lastOutcome = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(failure), [.. later], omitted: 3);
        Effect<int, string, EmptyEnvironment> first = new((_, _) => new(firstOutcome));
        Effect<int, string, EmptyEnvironment> last = new((_, _) => new(lastOutcome));
        var concurrency = parallel ? Concurrency.Parallel.WithMaxDegree(1) : Concurrency.Sequential;

        // When
        var all = await Effect.All([first, last], concurrency).RunExitAsync(CancellationToken.None);
        var errors = await Effect.AllErrors([first, last], concurrency).RunExitAsync(CancellationToken.None);
        var bound = await first.AndThen(_ => last).RunExitAsync(CancellationToken.None);
        var query = await (from x in first from y in last select x + y).RunExitAsync(CancellationToken.None);

        // Then
        foreach (IUnion exit in new IUnion[] { bound, query, all, errors })
        {
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Same(failure, die.Exception);
            Assert.Equal(earlier.Concat(later).Take(100), die.Suppressed);
            Assert.Equal(5 + Math.Max(0, earlierCount + laterCount - 100), die.OmittedSuppressed);
            for (var i = 0; i < Math.Min(100, earlierCount); i++) Assert.Same(earlier[i], die.Suppressed[i]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RaceOrRaceFirst_SuccessfulChildrenCarryContext_KeepsEachContextOnce(bool raceFirst)
    {
        // Given
        var context = new[]
        {
            new Suppressed(new Failed("left"), Relation.Sequential),
            new Suppressed(new Failed("right"), Relation.Consequence),
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var children = Enumerable.Range(0, 2).Select(i => new Effect<int, string, EmptyEnvironment>(async (_, _) =>
        {
            if (Interlocked.Increment(ref started) == 2) ready.SetResult();
            await ready.Task.ConfigureAwait(false);
            return Outcome<int, string>.Ok(i).WithSuppressed([context[i]], i + 2);
        })).ToArray();
        var failure = new IOException("projection");
        var winner = -1;

        // When
        var race = raceFirst ? Effect.RaceFirst(children[0], children[1]) : Effect.Race(children[0], children[1]);
        var exit = await race.Map<int>(value => { winner = value; throw failure; }).RunExitAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.InRange(winner, 0, 1);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(2, die.Suppressed.Count);
        foreach (var item in context) Assert.Contains(die.Suppressed, retained => ReferenceEquals(item, retained));
        Assert.Equal(5, die.OmittedSuppressed);
    }

}
