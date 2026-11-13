using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

public class RecurrenceInterruptionTests
{
    [Fact]
    public async Task Retry_CallerCancelsWhileThePolicyWaits_ReportsTheLastAttemptsErr()
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var effect = Effect<TimeEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await Task.Yield();
            attempts++;
            return Result.Err("unconfirmed");
        }).Retry(context => context.Gen<TimeEnvironment>(async scope =>
        {
            waiting.TrySetResult();
            await scope.Run(Effect.Sleep<TimeEnvironment>(TimeSpan.FromSeconds(10)));
            return true;
        }));

        // When
        var running = effect.RunExitAsync(new TimeEnvironment(new FakeTimeProvider()), caller.Token, options).AsTask();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        caller.Cancel();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(1, attempts);
        var failed = Assert.IsType<Failed>(Assert.Single(reported).Value);
        Assert.Equal("unconfirmed", failed.Error);
    }

    [Fact]
    public async Task Repeat_CallerCancelsWhileThePolicyWaits_ReportsNothing()
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ConcurrentQueue<ExitCase> reported = new();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<TimeEnvironment>.Sync(static () => 1).Repeat(context => context.Gen<TimeEnvironment>(async scope =>
        {
            waiting.TrySetResult();
            await scope.Run(Effect.Sleep<TimeEnvironment>(TimeSpan.FromSeconds(10)));
            return true;
        }));

        // When
        var running = effect.RunExitAsync(new TimeEnvironment(new FakeTimeProvider()), caller.Token, options).AsTask();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        caller.Cancel();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Empty(reported);
    }
}
