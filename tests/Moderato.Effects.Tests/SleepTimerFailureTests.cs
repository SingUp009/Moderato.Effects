using Microsoft.Extensions.Time.Testing;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class SleepTimerFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sleep_TimerCreationThrowsWithoutCancellation_ReturnsDie(bool cancellationException)
    {
        // Given
        Exception failure = cancellationException ? new OperationCanceledException() : new IOException("timer unavailable");
        var clock = new ThrowingTimerClock(failure);
        var sleep = Effect.Sleep<TimeEnvironment>(TimeSpan.FromSeconds(1));

        // When
        // None keeps caller isolation from masking Sleep's exception boundary.
        var exit = await sleep.RunExitAsync(new TimeEnvironment(clock), CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(1, clock.Calls);
    }

    [Fact]
    public async Task Sleep_TimerCreationCancelsCallerAndThrowsOce_IsInterrupted()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var clock = new ThrowingTimerClock(failure, cancellation.Cancel);
        var sleep = Effect.Sleep<TimeEnvironment>(TimeSpan.FromSeconds(1));

        // When
        var exit = await sleep.RunExitAsync(new TimeEnvironment(clock), cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(1, clock.Calls);
    }

    [Fact]
    public async Task RunExitAsync_SuspendedSleepCanceledByInnerContext_IsInterrupted()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var sleep = Effect.Sleep<TimeEnvironment>(TimeSpan.FromHours(1));
        // The inner token can cancel without adding caller isolation around the public run.
        Effect<Unit, Never, TimeEnvironment> work = new((env, _) => sleep.Run(env, new RunContext(cancellation.Token)));

        // When
        var running = work.RunExitAsync(new TimeEnvironment(clock), CancellationToken.None).AsTask();
        var finishedEarly = running.IsCompleted;
        await cancellation.CancelAsync();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.False(finishedEarly);
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    private sealed class ThrowingTimerClock(Exception failure, Action? beforeThrow = null) : TimeProvider
    {
        internal int Calls { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Calls++;
            beforeThrow?.Invoke();
            throw failure;
        }
    }
}
