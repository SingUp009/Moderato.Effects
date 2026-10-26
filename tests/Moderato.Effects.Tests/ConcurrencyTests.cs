namespace Moderato.Effects.Tests;

public class ConcurrencyTests
{
    [Fact]
    public void Concurrency_Default_IsSequential()
    {
        // Given
        Concurrency concurrency = default;

        // When
        var isSequential = concurrency == Concurrency.Sequential;

        // Then
        Assert.True(isSequential);
        Assert.False(concurrency.IsParallel);
    }

    [Fact]
    public void WithMaxDegree_OnParallel_LimitsTheDegree()
    {
        // Given
        var parallel = Concurrency.Parallel;

        // When
        var limited = parallel.WithMaxDegree(4);

        // Then
        Assert.True(limited.IsParallel);
        Assert.Equal(4, limited.MaxDegree);
    }

    [Fact]
    public void WithMaxDegree_OnSequential_Throws()
    {
        // Given
        var sequential = Concurrency.Sequential;

        // When
        void Act() => sequential.WithMaxDegree(4);

        // Then
        Assert.Throws<InvalidOperationException>(Act);
    }

    [Fact]
    public void WithMaxDegree_BelowOne_Throws()
    {
        // Given
        var parallel = Concurrency.Parallel;

        // When
        void Act() => parallel.WithMaxDegree(0);

        // Then
        Assert.Throws<ArgumentOutOfRangeException>(Act);
    }
}

public class AllTests
{
    private static Effect<Unit, Never, EmptyEnvironment> Record(List<string> log, string entry)
        => Effect<EmptyEnvironment>.Sync(() =>
        {
            lock (log) log.Add(entry);
            return Unit.Value;
        });

    private static Effect<int, AppError, EmptyEnvironment> Step(List<string> log, int n)
        => Effect<EmptyEnvironment>.Sync(() =>
        {
            log.Add($"step {n}");
            return n;
        });

    private static Effect<int, AppError, EmptyEnvironment> WaitUntilCanceled(List<string> log, string name, TaskCompletionSource started)
        => Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Record(log, $"{name} released"));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 0;
        });

    private static Effect<int, AppError, EmptyEnvironment> FailAfter(Task signal, AppError error)
        => Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await signal;
            throw scope.Fail(error);
        });

    [Fact]
    public async Task All_EverythingSucceeds_CollectsTheValuesInInputOrder()
    {
        // Given
        var log = new List<string>();
        var steps = new[] { Step(log, 1), Step(log, 2), Step(log, 3) };

        // When
        var result = await Effect.All(steps).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var values));
        Assert.Equal([1, 2, 3], values);
    }

    [Fact]
    public async Task All_EffectsOfDifferentTypes_ReturnsATuple()
    {
        // Given
        var user = Effect<EmptyEnvironment>.Succeed(new User(1, "ada"));
        var count = Effect<EmptyEnvironment>.Succeed(42);

        // When
        var result = await Effect.All(user, count).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var pair));
        Assert.Equal((new User(1, "ada"), 42), pair);
    }

    [Fact]
    public async Task All_NoEffects_SucceedsWithAnEmptyArray()
    {
        // Given
        var none = Array.Empty<Effect<int, AppError, EmptyEnvironment>>();

        // When
        var result = await Effect.All(none).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var values));
        Assert.Empty(values);
    }

    [Fact]
    public async Task All_Sequential_RunsOneAtATimeInOrder()
    {
        // Given
        var log = new List<string>();
        var steps = new[] { Step(log, 1), Step(log, 2), Step(log, 3) };

        // When
        await Effect.All(steps, Concurrency.Sequential).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(["step 1", "step 2", "step 3"], log);
    }

    [Fact]
    public async Task All_SequentialAndOneFails_DoesNotStartTheRest()
    {
        // Given
        var log = new List<string>();
        Effect<int, AppError, EmptyEnvironment> refused = Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("no"));
        var steps = new[] { Step(log, 1), refused, Step(log, 3) };

        // When
        var result = await Effect.All(steps).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int[], AppError>>(Result.Err<AppError>(new Forbidden("no")), result);
        Assert.Equal(["step 1"], log);
    }

    [Fact]
    public async Task All_Parallel_RunsTheEffectsAtTheSameTime()
    {
        // Given
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var a = Effect<EmptyEnvironment>.Async(async _ =>
        {
            first.SetResult();
            await second.Task;
            return 1;
        });
        var b = Effect<EmptyEnvironment>.Async(async _ =>
        {
            second.SetResult();
            await first.Task;
            return 2;
        });

        // When
        var result = await Effect.All([a, b], Concurrency.Parallel).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var values));
        Assert.Equal([1, 2], values);
    }

    [Fact]
    public async Task All_ParallelWithMaxDegree_NeverRunsMoreThanTheLimit()
    {
        // Given
        var running = 0;
        var peak = 0;
        var work = Enumerable.Range(0, 8).Select(i => Effect<EmptyEnvironment>.Async(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref running);
            return i;
        }));

        // When
        var result = await Effect.All(work, Concurrency.Parallel.WithMaxDegree(2)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var values));
        Assert.Equal(Enumerable.Range(0, 8), values);
        Assert.True(peak <= 2, $"peak was {peak}");

        static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    [Fact]
    public async Task All_ParallelAndOneFails_CancelsTheOthersAndWaitsForTheirReleases()
    {
        // Given
        var log = new List<string>();
        var started = new TaskCompletionSource();
        var slow = WaitUntilCanceled(log, "slow", started);
        var failing = FailAfter(started.Task, new NotFound(1));

        // When
        var result = await Effect.All([slow, failing], Concurrency.Parallel).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int[], AppError>>(Result.Err<AppError>(new NotFound(1)), result);
        Assert.Equal(["slow released"], log);
    }

    [Fact]
    public async Task All_SiblingsReleaseThrowsAfterTheCancellation_DiesKeepingTheErr()
    {
        // Given
        var started = new TaskCompletionSource();
        var slow = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("rollback failed")));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 0;
        });
        var failing = FailAfter(started.Task, new NotFound(1));

        // When
        var exit = await Effect.All([slow, failing], Concurrency.Parallel).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
        Assert.Contains(new Suppressed(new Failed(new NotFound(1)), Relation.Parallel), die.Suppressed);
    }

    [Fact]
    public async Task All_CallerCancels_IsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var started = new TaskCompletionSource();
        var slow = WaitUntilCanceled(log, "slow", started);

        // When
        var running = Effect.All([slow, slow], Concurrency.Parallel.WithMaxDegree(1)).RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await started.Task;
        await cts.CancelAsync();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(["slow released"], log);
    }
}

public class AllSettledTests
{
    [Fact]
    public async Task AllSettled_MixedEndings_ReturnsHowEachEnded()
    {
        // Given
        var bug = new InvalidOperationException("bug");
        Effect<int, AppError, EmptyEnvironment>[] effects =
        [
            Effect<EmptyEnvironment>.Succeed(1),
            Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(2)),
            Effect<EmptyEnvironment>.Sync<int>(() => throw bug),
            Effect<EmptyEnvironment>.Succeed(4),
        ];

        // When
        var result = await Effect.AllSettled(effects).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var exits));
        Assert.Equal<Exit<int, AppError>>(new Ok<int>(1), exits[0]);
        Assert.Equal<Exit<int, AppError>>(new Err<AppError>(new NotFound(2)), exits[1]);
        Assert.True(exits[2].TryGetValue(out Die? die));
        Assert.Same(bug, die.Exception);
        Assert.Equal<Exit<int, AppError>>(new Ok<int>(4), exits[3]);
    }
}

public class AllErrorsTests
{
    private static Effect<string, string, EmptyEnvironment> Validate(string field, string value)
        => value.Length > 0
            ? Effect<EmptyEnvironment>.Succeed(value)
            : Effect<EmptyEnvironment>.Fail($"{field} is required");

    [Fact]
    public async Task AllErrors_SeveralFail_CollectsEveryErrorInInputOrder()
    {
        // Given
        var fields = new[] { Validate("name", ""), Validate("email", "ada@example.com"), Validate("city", "") };

        // When
        var result = await Effect.AllErrors(fields).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var errors));
        Assert.Equal(["name is required", "city is required"], errors);
    }

    [Fact]
    public async Task AllErrors_AllSucceed_CollectsTheValues()
    {
        // Given
        var fields = new[] { Validate("name", "ada"), Validate("email", "ada@example.com") };

        // When
        var result = await Effect.AllErrors(fields).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var values));
        Assert.Equal(["ada", "ada@example.com"], values);
    }

    [Fact]
    public async Task AllErrors_OneThrows_Dies()
    {
        // Given
        var fields = new[] { Validate("name", ""), Effect<EmptyEnvironment>.Sync<string>(() => throw new FormatException("bad")) };

        // When
        var exit = await Effect.AllErrors(fields).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<FormatException>(die.Exception);
    }
}

public class OnThreadPoolTests
{
    [Fact]
    public async Task OnThreadPool_BlockingWork_DoesNotBlockTheCaller()
    {
        // Given
        using var gate = new ManualResetEventSlim();
        var blocking = Effect<EmptyEnvironment>.Sync(() =>
        {
            gate.Wait(TestContext.Current.CancellationToken);
            return 1;
        });

        // When
        var running = blocking.OnThreadPool().RunAsync(TestContext.Current.CancellationToken).AsTask();
        var returnedBeforeTheWork = !running.IsCompleted;
        gate.Set();
        var result = await running;

        // Then
        Assert.True(returnedBeforeTheWork);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
    }
}

[CollectionDefinition(nameof(DiagnosticsCollection), DisableParallelization = true)]
public sealed class DiagnosticsCollection;

[Collection(nameof(DiagnosticsCollection))]
public class EffectDiagnosticsTests
{
    // A fails; B is canceled because of it and then throws, so B's defect is suppressed under A's Err and has to be reported.
    private static Effect<int[], AppError, EmptyEnvironment> ErrWithACanceledSiblingThatThrows()
    {
        var started = new TaskCompletionSource();
        Effect<int, AppError, EmptyEnvironment> slow = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            throw new ObjectDisposedException("socket");
        });
        var failing = Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await started.Task;
            throw scope.Fail(new NotFound(1));
        });
        return Effect.All([slow, failing], Concurrency.Parallel);
    }

    [Fact]
    public async Task UnobservedFailureHandler_DefectCausedByTheCancellation_ReceivesItWhileTheErrIsTheResult()
    {
        // Given
        var reported = new List<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); };
        var effect = ErrWithACanceledSiblingThatThrows();

        try
        {
            // When
            var result = await effect.RunAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal<Result<int[], AppError>>(Result.Err<AppError>(new NotFound(1)), result);
            var die = Assert.IsType<Die>(Assert.Single(reported).Value);
            Assert.IsType<ObjectDisposedException>(die.Exception);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task UnobservedFailureHandler_HandlerThrows_DoesNotChangeTheResult()
    {
        // Given
        EffectDiagnostics.UnobservedFailureHandler = _ => throw new InvalidOperationException("broken handler");
        var effect = ErrWithACanceledSiblingThatThrows();

        try
        {
            // When
            var result = await effect.RunAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal<Result<int[], AppError>>(Result.Err<AppError>(new NotFound(1)), result);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task UnobservedFailureHandler_InterruptedRunWithASuppressedDefect_ReceivesTheDefect()
    {
        // Given
        var reported = new List<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); };
        using var cts = new CancellationTokenSource();
        using var bothStarted = new CountdownEvent(2);
        Effect<int, AppError, EmptyEnvironment> waits = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            bothStarted.Signal();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        });
        Effect<int, AppError, EmptyEnvironment> throwsWhenCanceled = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            bothStarted.Signal();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            throw new IOException("connection reset");
        });

        try
        {
            // When
            var running = Effect.All([waits, throwsWhenCanceled], Concurrency.Parallel).RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
            bothStarted.Wait(TestContext.Current.CancellationToken);
            await cts.CancelAsync();
            var exit = await running;

            // Then
            Assert.True(exit.TryGetValue(out Interrupted? _));
            var die = Assert.IsType<Die>(Assert.Single(reported).Value);
            Assert.IsType<IOException>(die.Exception);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }
}
