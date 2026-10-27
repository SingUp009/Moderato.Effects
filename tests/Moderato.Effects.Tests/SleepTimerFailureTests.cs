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
