using System.Collections.Concurrent;

namespace Moderato.Effects.Tests;

public class InterruptedContextTests
{
    private static Effect<int, string, EmptyEnvironment> TurnsCancellationIntoErr(CancellationTokenSource caller, bool turnsIntoErr)
        => Effect<EmptyEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            caller.Cancel();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (turnsIntoErr)
            {
                return Result.Err("unconfirmed");
            }
            return Result.Ok(0);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_InterruptedGroupDropsAnErr_ReportsTheErrToTheRunsHandler(bool returnExit)
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = Effect.All([TurnsCancellationIntoErr(caller, turnsIntoErr: true)]);

        // When
        var interrupted = false;
        if (returnExit)
        {
            var exit = await effect.RunExitAsync(EmptyEnvironment.Value, caller.Token, options);
            interrupted = exit.TryGetValue(out Interrupted? _);
        }
        else
        {
            try
            {
                await effect.RunAsync(EmptyEnvironment.Value, caller.Token, options);
            }
            catch (OperationCanceledException)
            {
                interrupted = true;
            }
        }

        // Then
        Assert.True(interrupted);
        var failed = Assert.IsType<Failed>(Assert.Single(reported).Value);
        Assert.Equal("unconfirmed", failed.Error);
    }

    [Fact]
    public async Task RunExitAsync_InterruptedWithoutContext_ReportsNothing()
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = Effect.All([TurnsCancellationIntoErr(caller, turnsIntoErr: false)]);

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, caller.Token, options);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Empty(reported);
    }

    [Fact]
    public async Task RunExitAsync_ErrKeptAsTheResult_IsNotReported()
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = TurnsCancellationIntoErr(caller, turnsIntoErr: true);

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, caller.Token, options);

        // Then
        Assert.Equal(new Err<string>("unconfirmed"), exit);
        Assert.Empty(reported);
    }
}
