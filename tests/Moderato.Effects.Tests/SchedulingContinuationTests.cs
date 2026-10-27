namespace Moderato.Effects.Tests;

public class SchedulingContinuationTests
{
    [Fact]
    public async Task Retry_ImmediateSchedule_ResolvesTheClockForEachWait()
    {
        // Given
        var env = new CountingClock();
        Effect<int, string, CountingClock> failed = Effect<CountingClock>.Fail("failed");

        // When
        var result = await failed.Retry(Schedule.Immediately.UpTo(2)).RunAsync(env, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("failed"), result);
        Assert.Equal(3, env.Resolutions);
        Assert.Equal(4, env.TimestampReads);
    }

    [Fact]
    public async Task Retry_ClockInitializationThrows_DoesNotStartTheBody()
    {
        // Given
        var defect = new InvalidOperationException("clock unavailable");
        var env = new ThrowingClock(1, defect);
        var calls = 0;
        var source = Effect<ThrowingClock>.Sync(() => ++calls);

        // When
        var exit = await source.Retry(Schedule.Immediately.UpTo(1)).RunExitAsync(env, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Retry_WaitClockResolutionThrows_KeepsTheLastError()
    {
        // Given
        var defect = new InvalidOperationException("clock unavailable");
        var env = new ThrowingClock(2, defect);
        Effect<int, string, ThrowingClock> source = Effect<ThrowingClock>.Fail("failed");

        // When
        var exit = await source.Retry(Schedule.Immediately.UpTo(1)).RunExitAsync(env, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(new Failed("failed"), Assert.Single(die.Suppressed).Case.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schedule_FilterThrows_KeepsTheLastErrorWhenRetrying(bool repeat)
    {
        // Given
        var defect = new InvalidOperationException("filter failed");
        Effect<int, string, DefaultEnvironment> source = repeat
            ? Effect<DefaultEnvironment>.Succeed(20)
            : Effect<DefaultEnvironment>.Fail("failed");
        Func<string, bool> retryFilter = _ => throw defect;
        Func<int, bool> repeatFilter = _ => throw defect;
        var effect = repeat ? source.Repeat(Schedule.Immediately.UpTo(1), repeatFilter)
            : source.Retry(Schedule.Immediately.UpTo(1), retryFilter);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        if (repeat) Assert.Empty(die.Suppressed);
        else Assert.Equal(new Failed("failed"), Assert.Single(die.Suppressed).Case.Value);
    }

    private sealed class CountingClock : TimeProvider, IHas<TimeProvider>
    {
        public int Resolutions { get; private set; }
        public int TimestampReads { get; private set; }

        TimeProvider IHas<TimeProvider>.Service
        {
            get
            {
                Resolutions++;
                return this;
            }
        }

        public override long GetTimestamp()
        {
            TimestampReads++;
            return 0;
        }
    }

    private sealed class ThrowingClock(int throwOn, Exception defect) : IHas<TimeProvider>
    {
        private int resolutions;

        TimeProvider IHas<TimeProvider>.Service => ++resolutions == throwOn ? throw defect : TimeProvider.System;
    }
}
