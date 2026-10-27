using Moderato.Results;

namespace Moderato.Effects.Tests;

public class CancellationLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_BodyEndsWhileCallerCallbackRuns_WaitsForCallbackBeforeReturning(bool nested)
    {
        // Given
        using var caller = new CancellationTokenSource();
        using var proceed = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEnded = 0;
        CancellationToken owned = default;
        var body = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            owned = ct;
            ct.Register(() =>
            {
                entered.TrySetResult();
                if (!proceed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                    throw new TimeoutException("Callback was not released.");
                Volatile.Write(ref callbackEnded, 1);
            });
            ready.SetResult();
            return await finish.Task;
        });
        var work = nested
            ? Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value), static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)).Use(_ => body)
            : body;

        // When
        var running = work.RunExitAsync(caller.Token).AsTask();
        Task canceling = Task.CompletedTask;
        var completedWhileBlocked = false;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            canceling = Task.Run(caller.Cancel, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            finish.SetResult(42);
            completedWhileBlocked = await Task.WhenAny(running, Task.Delay(50, TestContext.Current.CancellationToken)) == running;
        }
        finally
        {
            proceed.Set();
        }
        await canceling.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.False(completedWhileBlocked);
        Assert.Equal(1, Volatile.Read(ref callbackEnded));
        Assert.NotEqual(caller.Token, owned);
        Assert.True(owned.IsCancellationRequested);
        Assert.True(exit.TryGetValue(out Ok<int> ok));
        Assert.Equal(42, ok.Value);
    }
}
