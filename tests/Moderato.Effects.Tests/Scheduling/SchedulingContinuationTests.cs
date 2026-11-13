using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

public class SchedulingContinuationTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1, true)]
    public async Task RetryOrRepeat_ScheduleExhausted_DoesNotEvaluateFilter(bool repeat, int recurrences, bool elapsedLimit)
    {
        // Given
        var time = new FakeTimeProvider();
        var duration = TimeSpan.FromSeconds(1);
        var attempts = 0;
        var filterCalls = 0;
        var schedule = elapsedLimit ? Schedule.Immediately.During(duration) : Schedule.Immediately.UpTo(recurrences);
        Effect<int, string, TimeEnvironment> source = Effect<TimeEnvironment>.Sync(() =>
        {
            if (++attempts == 2 && elapsedLimit) time.Advance(duration);
            return attempts;
        });
        Effect<int, string, TimeEnvironment> effect = repeat
            ? source.Repeat(schedule, when: _ => Continue())
            : source.AndThen<int>(attempt => Effect<TimeEnvironment>.Fail($"attempt {attempt}"))
                .Retry(schedule, when: _ => Continue());

        bool Continue() => ++filterCalls <= recurrences ? true : throw new InvalidOperationException("schedule exhausted");

        // When
        var exit = await effect.RunExitAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(recurrences + 1, attempts);
        Assert.Equal(recurrences, filterCalls);
        if (repeat) Assert.Equal(attempts, Assert.IsType<Ok<int>>(exit.Value).Value);
        else Assert.Equal($"attempt {attempts}", Assert.IsType<Err<string>>(exit.Value).Error);
    }

    [Fact]
    public async Task Repeat_FilterConsumesRemainingTime_DoesNotStartAnotherAttempt()
    {
        // Given
        var time = new FakeTimeProvider();
        var duration = TimeSpan.FromSeconds(1);
        var attempts = 0;
        var effect = Effect<TimeEnvironment>.Sync(() => ++attempts).Repeat(Schedule.Immediately.During(duration), _ =>
        {
            time.Advance(duration);
            return true;
        });

        // When
        var exit = await effect.RunExitAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, Assert.IsType<Ok<int>>(exit.Value).Value);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retry_NullEnvironment_DiesWithArgumentNullException()
    {
        // Given
        Effect<int, string, CountingClock> source = Effect<CountingClock>.Fail("failed");
        var scheduled = source.Retry(Schedule.Immediately.UpTo(1));

        // When
        var exit = await scheduled.RunExitAsync(null!, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("environment", Assert.IsType<ArgumentNullException>(die.Exception).ParamName);
    }

    [Fact]
    public async Task Sleep_NullEnvironment_DiesWithArgumentNullException()
    {
        // Given
        var sleep = Effect.Sleep<CountingClock>(TimeSpan.Zero);

        // When
        var exit = await sleep.RunExitAsync(null!, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("environment", Assert.IsType<ArgumentNullException>(die.Exception).ParamName);
    }

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

    [Fact]
    public async Task Retry_FilterThrows_DiesWithTheLastErrorAsContext()
    {
        // Given
        var defect = new InvalidOperationException("filter failed");
        Effect<int, string, DefaultEnvironment> source = Effect<DefaultEnvironment>.Fail("failed");
        var effect = source.Retry(Schedule.Immediately.UpTo(1), _ => throw defect);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(new Failed("failed"), Assert.Single(die.Suppressed).Case.Value);
    }

    [Fact]
    public async Task Repeat_FilterThrows_DiesWithTheFilterDefect()
    {
        // Given
        var defect = new InvalidOperationException("filter failed");
        Effect<int, string, DefaultEnvironment> source = Effect<DefaultEnvironment>.Succeed(20);
        var effect = source.Repeat(Schedule.Immediately.UpTo(1), _ => throw defect);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Empty(die.Suppressed);
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
