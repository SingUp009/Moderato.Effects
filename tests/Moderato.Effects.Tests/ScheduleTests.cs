using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

public class ScheduleTests
{
    [Fact]
    public void UpTo_Negative_Throws()
    {
        // Given
        var schedule = Schedule.Spaced(TimeSpan.FromSeconds(1));

        // When
        void Act() => schedule.UpTo(-1);

        // Then
        Assert.Throws<ArgumentOutOfRangeException>(Act);
    }

    [Fact]
    public void Exponential_FactorBelowOne_Throws()
    {
        // Given
        var baseDelay = TimeSpan.FromMilliseconds(100);

        // When
        void Act() => Schedule.Exponential(baseDelay, factor: 0.5);

        // Then
        Assert.Throws<ArgumentOutOfRangeException>(Act);
    }
}

public class RetryTests
{
    private static readonly TimeSpan s_oneSecond = TimeSpan.FromSeconds(1);

    // Fails with NotFound(attempt) until the given attempt, then succeeds with the attempt number.
    private static Effect<int, AppError, R> SucceedsOnAttempt<R>(int succeedOn, Counter attempts)
    {
        Effect<int, AppError, R> attempt = Effect<R>.Sync(attempts.Next);
        return attempt.AndThen<int>(n => n >= succeedOn ? Effect<R>.Succeed(n) : Effect<R>.Fail<AppError>(new NotFound(n)));
    }

    [Fact]
    public async Task Retry_SucceedsAfterFailures_ReturnsTheValue()
    {
        // Given
        var clock = new RecordingClock();
        var attempts = new Counter();
        var flaky = SucceedsOnAttempt<TimeEnvironment>(3, attempts);

        // When
        var result = await flaky.Retry(Schedule.Spaced(s_oneSecond).UpTo(5)).RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Ok(3), result);
        Assert.Equal([s_oneSecond, s_oneSecond], clock.Waits);
    }

    [Fact]
    public async Task Retry_TriesRunOut_FailsWithTheLastErr()
    {
        // Given
        var clock = new RecordingClock();
        var attempts = new Counter();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, attempts);

        // When
        var result = await broken.Retry(Schedule.Spaced(s_oneSecond).UpTo(2)).RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new NotFound(3)), result);
        Assert.Equal(2, clock.Waits.Count);
    }

    [Fact]
    public async Task Retry_UpToZero_RunsOnce()
    {
        // Given
        var attempts = new Counter();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, attempts);

        // When
        var result = await broken.Retry(Schedule.Immediately.UpTo(0)).RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new NotFound(1)), result);
    }

    [Fact]
    public async Task Retry_ErrNotMatchingWhen_DoesNotRetry()
    {
        // Given
        var attempts = new Counter();
        Effect<int, AppError, TimeEnvironment> attempt = Effect<TimeEnvironment>.Sync(attempts.Next);
        var refused = attempt.AndThen(_ => (Effect<int, AppError, TimeEnvironment>)Effect<TimeEnvironment>.Fail<AppError>(new Forbidden("no")));

        // When
        var result = await refused.Retry(Schedule.Immediately.UpTo(3), when: e => e.Value is NotFound)
            .RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new Forbidden("no")), result);
        Assert.Equal(2, attempts.Next());
    }

    [Fact]
    public async Task Retry_Defect_IsNotRetried()
    {
        // Given
        var attempts = new Counter();
        var buggy = Effect<TimeEnvironment>.Sync<int>(() =>
        {
            attempts.Next();
            throw new InvalidOperationException("bug");
        });

        // When
        var exit = await buggy.Retry(Schedule.Immediately.UpTo(3)).RunExitAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(2, attempts.Next());
    }

    [Fact]
    public async Task Retry_Exponential_MultipliesTheWait()
    {
        // Given
        var clock = new RecordingClock();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, new Counter());

        // When
        await broken.Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).UpTo(3))
            .RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)], clock.Waits);
    }

    [Fact]
    public async Task Retry_WithMaxDelay_CapsTheWait()
    {
        // Given
        var clock = new RecordingClock();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, new Counter());

        // When
        await broken.Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).WithMaxDelay(TimeSpan.FromMilliseconds(250)).UpTo(4))
            .RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)],
            clock.Waits);
    }

    [Fact]
    public async Task Retry_During_StopsOnceTheTimeHasPassed()
    {
        // Given
        var clock = new RecordingClock();
        var attempts = new Counter();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, attempts);

        // When
        var result = await broken.Retry(Schedule.Spaced(s_oneSecond).During(TimeSpan.FromSeconds(2.5)))
            .RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new NotFound(4)), result);
        Assert.Equal(3, clock.Waits.Count);
    }

    [Fact]
    public async Task Retry_Jittered_SpreadsTheWaitAndRepeatsWithTheSameSeed()
    {
        // Given
        var broken = SucceedsOnAttempt<RetryEnvironment>(int.MaxValue, new Counter());
        var retried = broken.Retry(Schedule.Spaced(s_oneSecond).UpTo(5).Jittered());
        var firstClock = new RecordingClock();
        var secondClock = new RecordingClock();

        // When
        await retried.RunAsync(new RetryEnvironment(firstClock, new Random(42)), TestContext.Current.CancellationToken);
        await retried.RunAsync(new RetryEnvironment(secondClock, new Random(42)), TestContext.Current.CancellationToken);

        // Then
        Assert.All(firstClock.Waits, wait => Assert.InRange(wait, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2)));
        Assert.True(firstClock.Waits.Distinct().Count() > 1);
        Assert.Equal(firstClock.Waits, secondClock.Waits);
    }

    [Fact]
    public async Task Retry_ParallelRetriesShareARandomThatIsNotThreadSafe_EveryWaitStaysInRange()
    {
        // Given
        var clock = new RecordingClock();
        var env = new RetryEnvironment(clock, new Random(7));
        var retries = Enumerable.Range(0, 32).Select(_ =>
            SucceedsOnAttempt<RetryEnvironment>(int.MaxValue, new Counter()).Retry(Schedule.Spaced(s_oneSecond).UpTo(10).Jittered()).Attempt());

        // When
        await Effect.All(retries, Concurrency.Parallel).RunAsync(env, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(320, clock.Waits.Count);
        Assert.All(clock.Waits, wait => Assert.InRange(wait, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2)));
        Assert.True(clock.Waits.Distinct().Count() > 100);
    }

    [Fact]
    public async Task Retry_EachAttempt_ReleasesItsOwnResources()
    {
        // Given
        var log = new List<string>();
        var attempts = new Counter();
        Resource<int, AppError, TimeEnvironment> connection = Resource.Make(
            acquire: Effect<TimeEnvironment>.Sync(() =>
            {
                var n = attempts.Next();
                log.Add($"open {n}");
                return n;
            }),
            release: (n, _) => Effect<TimeEnvironment>.Sync(() =>
            {
                log.Add($"close {n}");
                return Unit.Value;
            }));
        var query = connection.Use<int>(n => n < 3 ? Effect<TimeEnvironment>.Fail<AppError>(new NotFound(n)) : Effect<TimeEnvironment>.Succeed(n));

        // When
        var result = await query.Retry(Schedule.Immediately.UpTo(5)).RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Ok(3), result);
        Assert.Equal(["open 1", "close 1", "open 2", "close 2", "open 3", "close 3"], log);
    }

    [Fact]
    public async Task Retry_RunTwice_StartsTheScheduleOver()
    {
        // Given
        var attempts = new Counter();
        var retried = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, attempts).Retry(Schedule.Immediately.UpTo(1));
        var env = new TimeEnvironment(new RecordingClock());

        // When
        await retried.RunAsync(env, TestContext.Current.CancellationToken);
        var second = await retried.RunAsync(env, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new NotFound(4)), second);
    }

    [Fact]
    public async Task Retry_Policy_DecidesAndWaitsFromTheContext()
    {
        // Given
        var clock = new RecordingClock();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, new Counter());

        // When
        var result = await broken.Retry(ctx => ctx.Gen<TimeEnvironment>(async scope =>
        {
            if (ctx.Attempt >= 3 || ctx.Error.Value is not NotFound) return false;
            await scope.Run(Effect.Sleep<TimeEnvironment>(TimeSpan.FromSeconds(ctx.Attempt)));
            return true;
        })).RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new NotFound(3)), result);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], clock.Waits);
    }

    [Fact]
    public async Task Retry_CallerCancelsWhileWaiting_IsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var broken = SucceedsOnAttempt<TimeEnvironment>(int.MaxValue, new Counter());

        // When
        var running = broken.Retry(Schedule.Spaced(TimeSpan.FromHours(1))).RunExitAsync(new TimeEnvironment(new FakeTimeProvider()), cts.Token).AsTask();
        await cts.CancelAsync();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }
}

public class RepeatTests
{
    private static Effect<string, AppError, TimeEnvironment> Poll(Queue<string> statuses)
        => Effect<TimeEnvironment>.Sync(statuses.Dequeue);

    [Fact]
    public async Task Repeat_WhileTheValueMatches_StopsAtTheFirstOtherValue()
    {
        // Given
        var clock = new RecordingClock();
        var statuses = new Queue<string>(["running", "running", "done", "never read"]);

        // When
        var result = await Poll(statuses).Repeat(Schedule.Spaced(TimeSpan.FromSeconds(1)), when: status => status == "running")
            .RunAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Ok("done"), result);
        Assert.Equal(2, clock.Waits.Count);
    }

    [Fact]
    public async Task Repeat_UpTo_ReturnsTheLastValue()
    {
        // Given
        var counter = new Counter();
        var tick = Effect<TimeEnvironment>.Sync(counter.Next);

        // When
        var result = await tick.Repeat(Schedule.Immediately.UpTo(2)).RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(3), result);
    }

    [Fact]
    public async Task Repeat_ErrInTheMiddle_StopsWithTheErr()
    {
        // Given
        var counter = new Counter();
        Effect<int, AppError, TimeEnvironment> count = Effect<TimeEnvironment>.Sync(counter.Next);
        var tick = count.AndThen<int>(n => n < 2 ? Effect<TimeEnvironment>.Succeed(n) : Effect<TimeEnvironment>.Fail<AppError>(new Forbidden("stopped")));

        // When
        var result = await tick.Repeat(Schedule.Immediately.UpTo(5)).RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new Forbidden("stopped")), result);
    }

    [Fact]
    public async Task Repeat_ManyTimes_RunsInALoopWithoutGrowingTheStack()
    {
        // Given
        var counter = new Counter();
        var tick = Effect<TimeEnvironment>.Sync(counter.Next);

        // When
        var result = await tick.Repeat(Schedule.Immediately.UpTo(100_000)).RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(100_001), result);
    }

    [Fact]
    public async Task Repeat_Policy_DecidesFromTheValue()
    {
        // Given
        var counter = new Counter();
        var tick = Effect<TimeEnvironment>.Sync(counter.Next);

        // When
        var result = await tick.Repeat(ctx => ctx.Gen<TimeEnvironment>(async _ =>
        {
            await Task.Yield();
            return ctx.Value < 4;
        })).RunAsync(new TimeEnvironment(new RecordingClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(4), result);
    }
}
