namespace Moderato.Effects.Tests;

file static class Steps
{
    internal static Effect<Unit, Never, EmptyEnvironment> Record(List<string> log, string entry)
        => Effect<EmptyEnvironment>.Sync(() =>
        {
            lock (log) log.Add(entry);
            return Unit.Value;
        });

    internal static Effect<string, AppError, EmptyEnvironment> WaitUntilCanceled(List<string> log, string name)
        => Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Record(log, $"{name} released"));
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return name;
        });

    internal static Effect<string, AppError, EmptyEnvironment> Fail(AppError error) => Effect<EmptyEnvironment>.Fail(error);
}

public class RaceTests
{
    [Fact]
    public async Task Race_OneSucceeds_TakesItAndWaitsForTheOtherToBeCanceled()
    {
        // Given
        var log = new List<string>();
        var slow = Steps.WaitUntilCanceled(log, "replica");
        var fast = Effect<EmptyEnvironment>.Succeed("primary");

        // When
        var result = await Effect.Race<string, AppError, EmptyEnvironment>(slow, fast).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Ok("primary"), result);
        Assert.Equal(["replica released"], log);
    }

    [Fact]
    public async Task Race_OneFails_WaitsForTheOtherToSucceed()
    {
        // Given
        var gate = new TaskCompletionSource();
        var failing = Scope<AppError, EmptyEnvironment>.Gen<string>(async scope =>
        {
            await Task.Yield();
            gate.SetResult();
            throw scope.Fail(new NotFound(1));
        });
        Effect<string, AppError, EmptyEnvironment> later = Effect<EmptyEnvironment>.Async(async _ =>
        {
            await gate.Task;
            return "replica";
        });

        // When
        var result = await Effect.Race(failing, later).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Ok("replica"), result);
    }

    [Fact]
    public async Task Race_BothFail_FailsWithOneOfTheErrors()
    {
        // Given
        var first = Steps.Fail(new NotFound(1));
        var second = Steps.Fail(new Forbidden("down"));

        // When
        var result = await Effect.Race(first, second).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.True(error.Value is NotFound or Forbidden);
    }

    [Fact]
    public async Task Race_OneDies_DiesAndCancelsTheOther()
    {
        // Given
        var log = new List<string>();
        var slow = Steps.WaitUntilCanceled(log, "replica");
        var broken = Effect<EmptyEnvironment>.Sync<string>(() => throw new InvalidOperationException("bug"));

        // When
        var exit = await Effect.Race<string, AppError, EmptyEnvironment>(slow, broken).RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal(["replica released"], log);
    }

    [Fact]
    public async Task Race_LosersReleaseThrows_DiesInsteadOfTakingTheWinner()
    {
        // Given
        var loser = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("close failed")));
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return "replica";
        });
        var winner = Effect<EmptyEnvironment>.Succeed("primary");

        // When
        var exit = await Effect.Race<string, AppError, EmptyEnvironment>(loser, winner).RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
    }

    [Fact]
    public async Task RaceFirst_FirstToFinishFails_FailsWithoutWaitingForTheOtherToSucceed()
    {
        // Given
        var log = new List<string>();
        var slow = Steps.WaitUntilCanceled(log, "replica");
        var failing = Steps.Fail(new Forbidden("down"));

        // When
        var result = await Effect.RaceFirst(slow, failing).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Err<AppError>(new Forbidden("down")), result);
        Assert.Equal(["replica released"], log);
    }

    [Fact]
    public async Task RaceFirst_FirstToFinishSucceeds_TakesIt()
    {
        // Given
        var log = new List<string>();
        var slow = Steps.WaitUntilCanceled(log, "replica");
        var fast = Effect<EmptyEnvironment>.Succeed("primary");

        // When
        var result = await Effect.RaceFirst<string, AppError, EmptyEnvironment>(slow, fast).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Ok("primary"), result);
    }

    [Fact]
    public async Task Race_CallerCancels_IsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var race = Effect.Race(Steps.WaitUntilCanceled(log, "a"), Steps.WaitUntilCanceled(log, "b"));

        // When
        var running = race.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }
}

public class ForkTests
{
    [Fact]
    public async Task Fork_Joined_GivesTheFibersValue()
    {
        // Given
        var work = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(Effect<EmptyEnvironment>.Succeed(21));
            var value = await scope.Run(fiber.Join());
            return value * 2;
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Ok(42), result);
    }

    [Fact]
    public async Task Fork_FiberWaitsForTheBlock_RunsAlongsideIt()
    {
        // Given
        var gate = new TaskCompletionSource();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(Effect<EmptyEnvironment>.Async(async _ =>
            {
                await gate.Task;
                return "fiber done";
            }));
            gate.SetResult();
            return await scope.Run(fiber.Join());
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, Never>>(Result.Ok("fiber done"), result);
    }

    [Fact]
    public async Task Fork_BlockEndsWithoutJoining_CancelsTheFiberAndWaitsForIt()
    {
        // Given
        var log = new List<string>();
        var work = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Fork(Steps.WaitUntilCanceled(log, "heartbeat"));
            return "done";
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Ok("done"), result);
        Assert.Equal(["heartbeat released"], log);
    }

    [Fact]
    public async Task Fork_FiberDiesBeforeTheJoin_EndsTheBlockWithTheFibersDefect()
    {
        // Given
        var bug = new InvalidOperationException("bug in fiber");
        var log = new List<string>();
        var work = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(exit => Steps.Record(log, $"block released ({exit.Value?.GetType().Name})"));
            await scope.Fork(Effect<EmptyEnvironment>.Async<int>(async _ =>
            {
                await Task.Yield();
                throw bug;
            }));
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return "never";
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(bug, die.Exception);
        Assert.Equal(["block released (Die)"], log);
    }

    [Fact]
    public async Task Fork_JoinedFiberFails_EndsTheBlockWithTheErrWidenedToTheUnion()
    {
        // Given
        var work = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork((Effect<User, NotFound, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail(new NotFound(7)));
            var user = await scope.Run(fiber.Join());
            return user.Name;
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Err<AppError>(new NotFound(7)), result);
    }

    [Fact]
    public async Task Join_FiberKeptAfterItsScopeEnded_RunAsyncThrowsCanceled()
    {
        // Given
        Fiber<int, Never, EmptyEnvironment>? kept = null;
        var forever = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await forever.Fork().Use(fiber =>
        {
            kept = fiber;
            return Effect<EmptyEnvironment>.Succeed(0);
        }).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // When
        async Task Act() => await kept!.Join().RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Act);
    }

    [Fact]
    public async Task Join_FiberKeptAfterItsScopeEnded_RunExitAsyncIsInterrupted()
    {
        // Given
        Fiber<int, Never, EmptyEnvironment>? kept = null;
        var forever = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await forever.Fork().Use(fiber =>
        {
            kept = fiber;
            return Effect<EmptyEnvironment>.Succeed(0);
        }).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // When
        var exit = await kept!.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task Join_FiberKeptAfterItsScopeEndedInsideGen_IsInterrupted()
    {
        // Given
        Fiber<int, Never, EmptyEnvironment>? kept = null;
        var forever = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await forever.Fork().Use(fiber =>
        {
            kept = fiber;
            return Effect<EmptyEnvironment>.Succeed(0);
        }).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var join = Scope<Never, EmptyEnvironment>.Gen(async scope => await scope.Run(kept!.Join()));

        // When
        var exit = await join.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task Gen_UnrelatedCancellationThrownByTheBody_Dies()
    {
        // Given
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(async _ =>
        {
            await Task.Yield();
            throw new OperationCanceledException("not this run's");
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<OperationCanceledException>(die.Exception);
    }

    [Fact]
    public async Task Fork_WithUse_JoinsInsideTheResourcesLifetime()
    {
        // Given
        var compute = Effect<EmptyEnvironment>.Succeed(42);

        // When
        var result = await compute.Fork().Use(fiber => fiber.Join()).RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(42), result);
    }
}

[Collection(nameof(DiagnosticsCollection))]
public class UnjoinedFiberTests
{
    [Fact]
    public async Task Fork_UnjoinedFiberFails_ReportsTheErrToTheDiagnosticsHandler()
    {
        // Given
        var reported = new List<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); };
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Fork(Steps.Fail(new Forbidden("cache refresh denied")));
            return "done";
        });

        try
        {
            // When
            var result = await work.RunAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            Assert.Equal<Result<string, Never>>(Result.Ok("done"), result);
            Assert.Equal(new Failed(new Forbidden("cache refresh denied")), Assert.Single(reported).Value);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }
}

public class ForkCleanupTests
{
    [Fact]
    public async Task Fork_TwoUnjoinedFibers_CancelsAndAwaitsEachInReverseOrder()
    {
        // Given
        TaskCompletionSource[] started =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
        var stopped = new List<int>();
        var children = Enumerable.Range(0, 2).Select(index => Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            started[index].SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                lock (stopped) stopped.Add(index);
            }

            return index;
        })).ToArray();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Fork(children[0]);
            await scope.Fork(children[1]);
            await Task.WhenAll(started.Select(signal => signal.Task));
            return 42;
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Exit<int, Never>>(new Ok<int>(42), exit);
        Assert.Equal([1, 0], stopped);
    }

    [Fact]
    public async Task Fork_FibersCancellationCallbackThrows_WaitsForTheFiberAndDiesWithTheCallbackDefect()
    {
        // Given
        var log = new List<string>();
        var registered = new TaskCompletionSource();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Fork(Scope<Never, EmptyEnvironment>.Gen(async child =>
            {
                child.Defer(_ => Steps.Record(log, "fiber released"));
                child.CancellationToken.Register(() => throw new InvalidOperationException("callback failed"));
                registered.SetResult();
                await Task.Delay(Timeout.Infinite, child.CancellationToken);
                return 0;
            }));
            await registered.Task;
            return "done";
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var loggedWhenTheBlockEnded = log.ToArray();

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("callback failed", die.Exception.Message);
        Assert.Equal(["fiber released"], loggedWhenTheBlockEnded);
    }

    [Fact]
    public async Task Join_CanceledBeforeTheFiberEnds_StillReportsTheFibersLaterReleaseFailure()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var fiberWork = Scope<Never, EmptyEnvironment>.Gen(async child =>
        {
            child.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("close failed")));
            started.SetResult();
            await gate.Task;
            return 1;
        });
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(fiberWork);
            return await scope.Run(fiber.Join());
        });

        // When
        var running = work.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        gate.SetResult();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
    }
}

[Collection(nameof(DiagnosticsCollection))]
public class RaceCanceledIntoErrTests
{
    private const string MESSAGE = "race child's release failed";

    // A child that turns the caller's cancellation into an Err, after a release whose own Race loser died.
    private static Effect<int, string, EmptyEnvironment> ErrOnCancelWithAFailingRelease(TaskCompletionSource started)
        => Scope<string, EmptyEnvironment>.Gen<int>(async scope =>
        {
            scope.Defer(_ => RaceWithDyingLoser());
            try
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw scope.Fail("canceled");
            }

            return 1;
        });

    private static Effect<Unit, Never, EmptyEnvironment> RaceWithDyingLoser()
    {
        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<Unit, Never, EmptyEnvironment> winner = Effect<EmptyEnvironment>.Async(async _ =>
        {
            await loserStarted.Task;
            return Unit.Value;
        });
        Effect<Unit, Never, EmptyEnvironment> loser = Effect<EmptyEnvironment>.Async<Unit>(async ct =>
        {
            try
            {
                loserStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(MESSAGE);
            }

            return Unit.Value;
        });
        return Effect.Race(winner, loser);
    }

    [Fact]
    public async Task Race_CallerCancelsAndBothChildrenEndWithErr_IsInterruptedAndReportsTheirDefects()
    {
        // Given
        var reported = Collect();
        using var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var race = Effect.Race(ErrOnCancelWithAFailingRelease(first), ErrOnCancelWithAFailingRelease(second));

        try
        {
            // When
            var running = race.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
            await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            Assert.True(exit.TryGetValue(out Interrupted? _));
            Assert.Equal(2, Ours(reported).Count);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task RaceFirst_CallerCancelsAndBothChildrenEndWithErr_IsInterruptedAndReportsTheirDefects()
    {
        // Given
        var reported = Collect();
        using var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var race = Effect.RaceFirst(ErrOnCancelWithAFailingRelease(first), ErrOnCancelWithAFailingRelease(second));

        try
        {
            // When
            var running = race.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
            await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            Assert.True(exit.TryGetValue(out Interrupted? _));
            Assert.Equal(2, Ours(reported).Count);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    private static List<ExitCase> Collect()
    {
        var reported = new List<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); };
        return reported;
    }

    // Other tests running at the same time may report their own failures to the handler.
    private static List<Die> Ours(List<ExitCase> reported)
    {
        lock (reported) return [.. reported.Select(static failure => failure.Value).OfType<Die>().Where(static die => die.Exception.Message == MESSAGE)];
    }
}

public class JoinSuppressedTests
{
    // A child that dies after registering releases that each fail, so its defect carries their failures.
    private static Effect<int, string, EmptyEnvironment> DiesAfterFailedReleases(int releaseFailures, Func<Exception> cleanup)
        => Scope<string, EmptyEnvironment>.Gen<int>(scope =>
        {
            for (var i = 0; i < releaseFailures; i++)
            {
#pragma warning disable MEFF305
                scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw cleanup()));
#pragma warning restore MEFF305
            }

            throw new InvalidOperationException("child");
        });

    // A race whose loser fails once it is canceled, so the scope keeps that failure as suppressed.
    private static Effect<int, string, EmptyEnvironment> RaceWhoseLoserFails(Exception failure)
    {
        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> winner = Effect<EmptyEnvironment>.Async(async _ =>
        {
            await loserStarted.Task;
            return 1;
        });
        Effect<int, string, EmptyEnvironment> loser = Effect<EmptyEnvironment>.Async<int>(async token =>
        {
            loserStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
            }

            throw failure;
        });
        return Effect.Race(winner, loser);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(120, 100, 20)]
    public async Task Join_ChildDefectAlreadyReportedToTheParent_KeepsItsSuppressedFailuresOnce(int releaseFailures, int kept, int omitted)
    {
        // Given
        var child = DiesAfterFailedReleases(releaseFailures, static () => new IOException("cleanup"));
        var parent = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(child);
            await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return await scope.Run(fiber.Join());
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("child", die.Exception.Message);
        Assert.Equal(kept, die.Suppressed.Count);
        Assert.All(die.Suppressed, s => Assert.IsType<IOException>(Assert.IsType<Die>(s.Case.Value).Exception));
        Assert.Equal(omitted, die.OmittedSuppressed);
    }

    [Fact]
    public async Task Join_ChildDefectAlreadyReportedToTheParent_GivesTheDefectWhileTheParentKeepsItsSuppressedFailures()
    {
        // Given
        var child = DiesAfterFailedReleases(1, static () => new IOException("cleanup"));
        Exit<int, string>? joined = null;
        var parent = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(child);
            await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            joined = await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return 0;
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(joined!.Value.TryGetValue(out Die? joinedDie));
        Assert.Equal("child", joinedDie.Exception.Message);
        Assert.Empty(joinedDie.Suppressed);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(joinedDie.Exception, die.Exception);
        Assert.Equal("cleanup", Assert.IsType<Die>(Assert.Single(die.Suppressed).Case.Value).Exception.Message);
    }

    [Theory]
    [InlineData(99, 119)]
    [InlineData(100, 120)]
    public async Task Join_ChildDefectAfterTheParentsOwnSuppressedFailures_CountsItsOmittedFailuresOnce(int parentFailures, int omitted)
    {
        // Given
        var child = DiesAfterFailedReleases(120, static () => new IOException("cleanup"));
        var parent = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            for (var i = 0; i < parentFailures; i++)
                await scope.Run(RaceWhoseLoserFails(new IOException("parent")));
            var fiber = await scope.Fork(child);
            await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return await scope.Run(fiber.Join());
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("child", die.Exception.Message);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(omitted, die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(99, 119)]
    [InlineData(100, 120)]
    public async Task Join_ComposedAfterAnEffectWithItsOwnSuppressedFailures_CountsTheChildsOmittedFailuresOnce(int earlierFailures, int omitted)
    {
        // Given
        var startChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> waitToStart = Effect<EmptyEnvironment>.Async(async _ =>
        {
            await startChild.Task;
            return 0;
        });
        var child = waitToStart.AndThen(_ => DiesAfterFailedReleases(120, static () => new IOException("cleanup")));
        var parent = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(child);
            Effect<int, string, EmptyEnvironment> earlier = Effect<EmptyEnvironment>.Succeed(0);
            for (var i = 0; i < earlierFailures; i++)
                earlier = earlier.AndThen(_ => RaceWhoseLoserFails(new IOException("parent")));
            Effect<int, string, EmptyEnvironment> childEnded = Effect<EmptyEnvironment>.Async(async _ =>
            {
                startChild.SetResult();
                await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                return 0;
            });
            return await scope.Run(earlier.AndThen(_ => childEnded).AndThen(_ => fiber.Join()));
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("child", die.Exception.Message);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(omitted, die.OmittedSuppressed);
    }

    [Fact]
    public async Task Join_SameChildDefectJoinedTwice_CountsItsSuppressedFailuresOnce()
    {
        // Given
        var child = DiesAfterFailedReleases(120, static () => new IOException("cleanup"));
        var parent = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(child);
            await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    await scope.Run(fiber.Join());
                }
                catch (InvalidOperationException exception) when (exception.Message == "child")
                {
                }
            }

            return 0;
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("child", die.Exception.Message);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(20, die.OmittedSuppressed);
    }

    [Fact]
    public async Task Join_ParentAlsoSuppressedTheSameException_KeepsTheParentsOwnFailure()
    {
        // Given
        var cleanup = new IOException("cleanup");
        var child = DiesAfterFailedReleases(1, () => cleanup);
        var parent = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Run(RaceWhoseLoserFails(cleanup));
            var fiber = await scope.Fork(child);
            await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return await scope.Run(fiber.Join());
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("child", die.Exception.Message);
        Assert.Equal(2, die.Suppressed.Count);
        Assert.All(die.Suppressed, s => Assert.Same(cleanup, Assert.IsType<Die>(s.Case.Value).Exception));
        Assert.Equal([Relation.Sequential, Relation.Consequence], die.Suppressed.Select(s => s.Relation).Order());
    }
}
