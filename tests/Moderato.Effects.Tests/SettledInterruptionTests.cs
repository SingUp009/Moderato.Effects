namespace Moderato.Effects.Tests;

public class SettledInterruptionTests
{
    [Theory]
    [InlineData(0, "Ok", false)]
    [InlineData(0, "Err", false)]
    [InlineData(0, "Die", false)]
    [InlineData(1, "Ok", false)]
    [InlineData(1, "Err", false)]
    [InlineData(1, "Die", false)]
    [InlineData(2, "Ok", false)]
    [InlineData(2, "Err", false)]
    [InlineData(2, "Die", false)]
    [InlineData(0, "Ok", true)]
    [InlineData(0, "Err", true)]
    [InlineData(0, "Die", true)]
    [InlineData(1, "Ok", true)]
    [InlineData(1, "Err", true)]
    [InlineData(1, "Die", true)]
    [InlineData(2, "Ok", true)]
    [InlineData(2, "Err", true)]
    [InlineData(2, "Die", true)]
    public async Task AllSettled_ChildInterruptedWithoutCallerCancellation_CollectsEveryExitInInputOrder(int mode, string otherKind, bool interruptedFirst)
    {
        // Given
        Fiber<int, Never, EmptyEnvironment>? kept = null;
        var forever = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await forever.Fork().Use(fiber =>
        {
            kept = fiber;
            return Effect<EmptyEnvironment>.Succeed(0);
        }).RunAsync(TestContext.Current.CancellationToken);
        var defect = new InvalidOperationException("defect");
        Effect<int, string, EmptyEnvironment> other = otherKind switch
        {
            "Err" => Effect<EmptyEnvironment>.Fail("invalid"),
            "Die" => Effect<EmptyEnvironment>.Sync<int>(() => throw defect),
            _ => Effect<EmptyEnvironment>.Succeed(42),
        };
        Effect<int, string, EmptyEnvironment> interrupted = kept!.Join();
        Effect<int, string, EmptyEnvironment>[] effects = interruptedFirst ? [interrupted, other] : [other, interrupted];
        var concurrency = mode switch
        {
            0 => Concurrency.Sequential,
            1 => Concurrency.Parallel,
            _ => Concurrency.Parallel.WithMaxDegree(1),
        };

        // When
        var exit = await Effect.AllSettled(effects, concurrency)
            .RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var exits = Assert.IsType<Ok<Exit<int, string>[]>>(exit.Value).Value;
        Assert.Equal(2, exits.Length);
        Assert.IsType<Interrupted>(exits[interruptedFirst ? 0 : 1].Value);
        var otherExit = exits[interruptedFirst ? 1 : 0].Value;
        switch (otherKind)
        {
            case "Err": Assert.Equal("invalid", Assert.IsType<Err<string>>(otherExit).Error); break;
            case "Die": Assert.Same(defect, Assert.IsType<Die>(otherExit).Exception); break;
            default: Assert.Equal(42, Assert.IsType<Ok<int>>(otherExit).Value); break;
        }
    }
}
