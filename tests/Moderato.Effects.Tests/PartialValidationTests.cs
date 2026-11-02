namespace Moderato.Effects.Tests;

public class PartialValidationTests
{
    [Theory]
    [InlineData(0, "Ok")]
    [InlineData(0, "Err")]
    [InlineData(1, "Ok")]
    [InlineData(1, "Err")]
    [InlineData(2, "Ok")]
    [InlineData(2, "Err")]
    [InlineData(0, "Die")]
    [InlineData(1, "Die")]
    [InlineData(2, "Die")]
    public async Task AllErrors_ChildInterruptedWithoutCallerCancellation_DoesNotReturnPartialValidation(int mode, string otherKind)
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
        Effect<int, string, EmptyEnvironment> first = otherKind switch
        {
            "Err" => Effect<EmptyEnvironment>.Fail("invalid"),
            "Die" => Effect<EmptyEnvironment>.Sync<int>(() => throw defect),
            _ => Effect<EmptyEnvironment>.Succeed(1),
        };
        Effect<int, string, EmptyEnvironment> interrupted = kept!.Join();
        var concurrency = mode switch
        {
            0 => Concurrency.Sequential,
            1 => Concurrency.Parallel,
            _ => Concurrency.Parallel.WithMaxDegree(1),
        };

        // When
        var exit = await Effect.AllErrors([first, interrupted], concurrency)
            .RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        if (otherKind == "Die") Assert.Same(defect, Assert.IsType<Die>(exit.Value).Exception);
        else Assert.IsType<Interrupted>(exit.Value);
    }
}
