using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

public class SleepTests
{
    [Fact]
    public async Task Sleep_ClockReachesTheDuration_Completes()
    {
        // Given
        var time = new FakeTimeProvider();
        var nap = Effect.Sleep<TimeEnvironment>(TimeSpan.FromSeconds(5));

        // When
        var running = nap.RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromSeconds(4));
        var finishedEarly = running.IsCompleted;
        time.Advance(TimeSpan.FromSeconds(1));

        // Then
        Assert.False(finishedEarly);
        Assert.Equal<Result<Unit, Never>>(Result.Ok(Unit.Value), await running);
    }

    [Fact]
    public async Task Sleep_CallerCancels_IsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var nap = Effect.Sleep<TimeEnvironment>(TimeSpan.FromHours(1));

        // When
        var running = nap.RunExitAsync(new TimeEnvironment(new FakeTimeProvider()), cts.Token).AsTask();
        await cts.CancelAsync();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public void Sleep_NegativeDuration_Throws()
    {
        // Given
        var duration = TimeSpan.FromSeconds(-1);

        // When
        void Act() => Effect.Sleep<TimeEnvironment>(duration);

        // Then
        Assert.Throws<ArgumentOutOfRangeException>(Act);
    }
}

public class TimeoutTests
{
    private static readonly TimeSpan s_oneSecond = TimeSpan.FromSeconds(1);

    private static Effect<User, RequestError, TimeEnvironment> SlowFindUser(TimeSpan delay)
        => Scope<RequestError, TimeEnvironment>.Gen(async scope =>
        {
            await scope.Run(Effect.Sleep<TimeEnvironment>(delay));
            return new User(1, "ada");
        });

    [Fact]
    public async Task Timeout_FinishesInTime_KeepsTheValue()
    {
        // Given
        var time = new FakeTimeProvider();
        var findUser = SlowFindUser(TimeSpan.FromMilliseconds(500));

        // When
        var running = findUser.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(500));
        var result = await running;

        // Then
        Assert.True(result.TryGetOk(out var user));
        Assert.Equal("ada", user.Name);
    }

    [Fact]
    public async Task Timeout_TimeRunsOut_FailsWithTheTimeoutCaseOfTheUnion()
    {
        // Given
        var time = new FakeTimeProvider();
        var findUser = SlowFindUser(TimeSpan.FromMinutes(1));

        // When
        var running = findUser.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var result = await running;

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new TimeoutError(s_oneSecond), error.Value);
    }

    [Fact]
    public async Task Timeout_EffectThatCannotFail_FailsWithTimeoutError()
    {
        // Given
        var time = new FakeTimeProvider();
        var nap = Effect.Sleep<TimeEnvironment>(TimeSpan.FromMinutes(1));

        // When
        var running = nap.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        Result<Unit, TimeoutError> result = await running;

        // Then
        Assert.Equal<Result<Unit, TimeoutError>>(Result.Err(new TimeoutError(s_oneSecond)), result);
    }

    [Fact]
    public async Task Timeout_WithOnTimeout_FailsWithTheMappedError()
    {
        // Given
        var time = new FakeTimeProvider();
        Effect<Unit, string, TimeEnvironment> nap = Effect.Sleep<TimeEnvironment>(TimeSpan.FromMinutes(1));

        // When
        var running = nap.Timeout(s_oneSecond, onTimeout: t => $"gave up after {t.After.TotalSeconds}s")
            .RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var result = await running;

        // Then
        Assert.Equal<Result<Unit, string>>(Result.Err("gave up after 1s"), result);
    }

    [Fact]
    public async Task TimeoutResult_TimeRunsOut_SucceedsWithTheTimeoutAsAValue()
    {
        // Given
        var time = new FakeTimeProvider();
        var findUser = SlowFindUser(TimeSpan.FromMinutes(1));

        // When
        var running = findUser.TimeoutResult(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var result = await running;

        // Then
        Assert.True(result.TryGetOk(out var inner));
        Assert.Equal<Result<User, TimeoutError>>(Result.Err(new TimeoutError(s_oneSecond)), inner);
    }

    [Fact]
    public async Task TimeoutResult_FinishesInTime_SucceedsWithTheValue()
    {
        // Given
        var findUser = SlowFindUser(TimeSpan.Zero);

        // When
        var result = await findUser.TimeoutResult(s_oneSecond).RunAsync(new TimeEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var inner));
        Assert.Equal<Result<User, TimeoutError>>(Result.Ok(new User(1, "ada")), inner);
    }

    [Fact]
    public async Task Timeout_EffectFailsInTime_KeepsTheError()
    {
        // Given
        Effect<User, RequestError, TimeEnvironment> findUser = Effect<TimeEnvironment>.Fail<RequestError>(new NotFound(7));

        // When
        var result = await findUser.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(7), error.Value);
    }

    [Fact]
    public async Task Timeout_TimeRunsOut_ReturnsOnlyAfterTheReleasesHaveRun()
    {
        // Given
        var time = new FakeTimeProvider();
        var log = new List<string>();
        var work = Scope<TimeoutError, TimeEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<TimeEnvironment>.Sync(() => { log.Add("released"); return Unit.Value; }));
            await scope.Run(Effect.Sleep<TimeEnvironment>(TimeSpan.FromMinutes(1)));
            return 1;
        });

        // When
        var running = work.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var result = await running;

        // Then
        Assert.Equal<Result<int, TimeoutError>>(Result.Err(new TimeoutError(s_oneSecond)), result);
        Assert.Equal(["released"], log);
    }

    [Fact]
    public async Task Timeout_EffectIgnoresCancellation_WaitsForItToFinish()
    {
        // Given
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource();
        var stubborn = Effect<TimeEnvironment>.Async(async _ =>
        {
            await gate.Task;
            return 1;
        });

        // When
        var running = stubborn.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var returnedAtTheLimit = running.IsCompleted;
        gate.SetResult();
        var result = await running;

        // Then
        Assert.False(returnedAtTheLimit);
        Assert.Equal<Result<int, TimeoutError>>(Result.Err(new TimeoutError(s_oneSecond)), result);
    }

    [Fact]
    public async Task Timeout_EffectThrowsOperationCanceledAfterTheLimit_FailsWithTimeoutError()
    {
        // Given
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource();
        var work = Effect<TimeEnvironment>.Async<int>(async ct =>
        {
            await gate.Task;
            ct.ThrowIfCancellationRequested();
            return 1;
        });

        // When
        var running = work.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        gate.SetResult();
        var result = await running;

        // Then
        Assert.Equal<Result<int, TimeoutError>>(Result.Err(new TimeoutError(s_oneSecond)), result);
    }

    [Fact]
    public async Task Timeout_EffectThrowsAnotherExceptionAfterTheLimit_StillFailsWithTimeoutError()
    {
        // Given
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource();
        var work = Effect<TimeEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            throw new ObjectDisposedException("socket");
        });

        // When
        var running = work.Timeout(s_oneSecond).RunAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        gate.SetResult();
        var result = await running;

        // Then
        Assert.Equal<Result<int, TimeoutError>>(Result.Err(new TimeoutError(s_oneSecond)), result);
    }

    [Fact]
    public async Task Timeout_ReleaseThrowsAfterTheLimit_DiesWithTheTimeoutAsSuppressed()
    {
        // Given
        var time = new FakeTimeProvider();
        var work = Scope<TimeoutError, TimeEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<TimeEnvironment>.Sync<Unit>(() => throw new IOException("rollback failed")));
            await scope.Run(Effect.Sleep<TimeEnvironment>(TimeSpan.FromMinutes(1)));
            return 1;
        });

        // When
        var running = work.Timeout(s_oneSecond).RunExitAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
        Assert.Equal([new Suppressed(new Failed(new TimeoutError(s_oneSecond)), Relation.Sequential)], die.Suppressed);
    }

    [Fact]
    public async Task Timeout_ReleaseInANestedGenThrowsAfterTheLimit_DiesWithTheReleaseDefect()
    {
        // Given
        var time = new FakeTimeProvider();
        var inner = Scope<TimeoutError, TimeEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<TimeEnvironment>.Sync<Unit>(() => throw new IOException("rollback failed")));
            await scope.Run(Effect.Sleep<TimeEnvironment>(TimeSpan.FromMinutes(1)));
            return 1;
        });
        var outer = Scope<TimeoutError, TimeEnvironment>.Gen(async scope => await scope.Run(inner) + 1);

        // When
        var running = outer.Timeout(s_oneSecond).RunExitAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken).AsTask();
        time.Advance(s_oneSecond);
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
    }

    [Fact]
    public async Task Timeout_CallerCancelsAfterTheLimitPassed_IsInterrupted()
    {
        // Given
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var work = Effect<TimeEnvironment>.Async<int>(async ct =>
        {
            await gate.Task;
            ct.ThrowIfCancellationRequested();
            return 1;
        });

        // When
        var running = work.Timeout(s_oneSecond).RunExitAsync(new TimeEnvironment(time), cts.Token).AsTask();
        time.Advance(s_oneSecond);
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public void Timeout_NegativeLimit_Throws()
    {
        // Given
        var nap = Effect.Sleep<TimeEnvironment>(s_oneSecond);

        // When
        void Act() => nap.Timeout(TimeSpan.FromSeconds(-1));

        // Then
        Assert.Throws<ArgumentOutOfRangeException>(Act);
    }
}
