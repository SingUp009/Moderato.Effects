using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class TimeoutOrderTests
{
    [Fact]
    public async Task Timeout_DisposalDoesNotWaitForCallback_PreservesEarlierIndependentFailure()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            // Given
            var earlier = Outcome<int, string>.Err("earlier");
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = new NonWaitingClock();
            Effect<int, string, TimeEnvironment> first = new(async (_, _) =>
            {
                await observed.Task.ConfigureAwait(false);
                return earlier;
            });
            var timed = Effect<TimeEnvironment>.Sync(() =>
            {
                clock.StartAndWaitForReason();
                return 42;
            }).Timeout(TimeSpan.FromSeconds(1)).MapErr(_ => "timeout")
                .TapErr(_ => observed.TrySetResult());

            // When
            var exit = await Effect.All([first, timed], Concurrency.Parallel)
                .RunExitAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await clock.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            Assert.Equal("earlier", Assert.IsType<Err<string>>(exit.Value).Error);
        }
    }

    [Fact]
    public async Task Timeout_TimerCallsBackAgain_PreservesFirstExpirationOrder()
    {
        // Given
        var later = default(Outcome<int, string>);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new NonWaitingClock(repeatOnDisposal: true);
        Effect<int, string, TimeEnvironment> other = new(async (_, _) =>
        {
            await observed.Task.ConfigureAwait(false);
            return later;
        });
        var timed = Effect<TimeEnvironment>.Async<int>(async _ =>
        {
            clock.StartAndWaitForReason();
            await clock.Completed.ConfigureAwait(false);
            later = Outcome<int, string>.Err("later");
            return 42;
        }).Timeout(TimeSpan.FromSeconds(1)).MapErr(_ => "timeout")
            .TapErr(_ => observed.TrySetResult());

        // When
        var exit = await Effect.All([other, timed], Concurrency.Parallel)
            .RunExitAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal("timeout", Assert.IsType<Err<string>>(exit.Value).Error);
    }

    private sealed class NonWaitingClock(bool repeatOnDisposal = false) : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => completed.Task;

        public void StartAndWaitForReason()
        {
            new Thread(() =>
            {
                try
                {
                    callback!(state);
                    completed.TrySetResult();
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            })
            { IsBackground = true }.Start();
            if (!SpinWait.SpinUntil(() => ((Cancellation)state!).Reason == CancellationReason.Timeout, TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The timer did not publish its cancellation reason.");
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            return new NonWaitingTimer(repeatOnDisposal ? () => callback(state) : null);
        }

        private sealed class NonWaitingTimer(Action? onDispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => onDispose?.Invoke();
            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
