using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class RaceContextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task Race_WinnerAndLoserCarryContext_KeepsWinnerPrefixAndOmittedCounts(int winnerCount)
    {
        // Given
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("projection failed");
        var winnerContext = Enumerable.Range(0, winnerCount).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        Suppressed[] loserContext = [new(new Failed("first"), Relation.Consequence), new(new Failed("second"), Relation.Parallel)];
        var won = Outcome<int, string>.Ok(1).WithSuppressed([.. winnerContext], 2);
        var lost = Outcome<int, string>.Ok(2).WithSuppressed([.. loserContext], 3);
        var winner = new Effect<int, string, EmptyEnvironment>(async (_, _) =>
        {
            await started.Task;
            return won;
        });
        var loser = new Effect<int, string, EmptyEnvironment>(async (_, context) =>
        {
            using var registration = context.CancellationToken.Register(() => stopped.SetResult());
            started.SetResult();
            await stopped.Task;
            return lost;
        });
        Suppressed[] expected = [.. winnerContext, .. loserContext];
        var winnerValue = 0;

        // When
        var exit = await Effect.Race(winner, loser).Map<int>(value => { winnerValue = value; throw failure; })
            .RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(1, winnerValue);
        Assert.Equal(Math.Min(100, expected.Length), die.Suppressed.Count);
        Assert.Equal(5 + Math.Max(0, expected.Length - 100), die.OmittedSuppressed);
        for (var i = 0; i < die.Suppressed.Count; i++) Assert.Same(expected[i], die.Suppressed[i]);
    }
}
