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

    [Fact]
    public async Task RunExitAsync_TimeoutCancelsWorkThatTurnsItIntoErr_ReportsThatErr()
    {
        // Given
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<TimeEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                return Result.Err("unconfirmed");
            }
            return Result.Ok(0);
        }).Timeout(TimeSpan.FromSeconds(1), static _ => "timeout");

        // When
        var running = effect.RunExitAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken, options).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(new Err<string>("timeout"), exit);
        var failed = Assert.IsType<Failed>(Assert.Single(reported).Value);
        Assert.Equal("unconfirmed", failed.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_ErrWithASuppressedErrIsTheResultOrHandled_ReportsTheSuppressedErrOnce(bool handled)
    {
        // Given
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await siblingStarted.Task;
            return Result.Err("first");
        });
        var sibling = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            siblingStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                return Result.Err("sibling");
            }
            return Result.Ok(0);
        });
        var group = Effect.All([failing, sibling], Concurrency.Parallel).Map(static values => values.Length);
        var effect = handled ? group.OrElse(static _ => (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(0)) : group;

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken, options);

        // Then
        Assert.Equal(handled ? new Ok<int>(0) : new Err<string>("first"), exit);
        var failed = Assert.IsType<Failed>(Assert.Single(reported).Value);
        Assert.Equal("sibling", failed.Error);
    }
}
