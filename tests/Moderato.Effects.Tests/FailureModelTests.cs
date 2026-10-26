namespace Moderato.Effects.Tests;

public class FailureModelTests
{
    [Fact]
    public async Task RunExitAsync_BodyThrowsAnotherExceptionAfterTheCallerCancels_ReturnsDie()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var read = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            throw new IOException("stream closed");
        });

        // When
        var running = read.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
    }

    [Fact]
    public async Task RunExitAsync_AnotherTokensCancellationAfterTheCallerCancels_ReturnsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        using var service = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var call = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            await service.CancelAsync();
            service.Token.ThrowIfCancellationRequested();
            return 1;
        });

        // When
        var running = call.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task RunExitAsync_CancellationWithoutATokenAfterTheCallerCancels_ReturnsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var call = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            throw new OperationCanceledException();
        });

        // When
        var running = call.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task RunExitAsync_ManyReleasesFail_KeepsAHundredSuppressedAndCountsTheRest()
    {
        // Given
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            for (var i = 0; i < 150; i++)
            {
                var n = i;
                // Registers many releases on purpose to exceed the suppressed limit.
#pragma warning disable MEFF305
                scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException($"release {n}")));
#pragma warning restore MEFF305
            }

            await Task.Yield();
            return 1;
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("release 149", die.Exception.Message);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(49, die.OmittedSuppressed);
    }

    [Fact]
    public async Task RunExitAsync_TwoForkedFibersDieWithOmittedSuppressed_CountsEveryOmittedFailure()
    {
        // Given
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var first = await scope.Fork(FailingReleases(120));
            var second = await scope.Fork(FailingReleases(130));
            // Ignores the canceled body token, so both defects arrive before the body ends.
            await first.Join().RunExitAsync(CancellationToken.None);
            await second.Join().RunExitAsync(CancellationToken.None);
            return 1;
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(250 - 1 - 100, die.OmittedSuppressed);
    }

    [Fact]
    public async Task RunExitAsync_OneForkedFiberDiesWithOmittedSuppressed_KeepsItsCount()
    {
        // Given
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(FailingReleases(120));
            await fiber.Join().RunExitAsync(CancellationToken.None);
            return 1;
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("release 119", die.Exception.Message);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(19, die.OmittedSuppressed);
    }

    private static Effect<int, Never, EmptyEnvironment> FailingReleases(int count) => Scope<Never, EmptyEnvironment>.Gen(async scope =>
    {
        for (var i = 0; i < count; i++)
        {
            var n = i;
            // Registers many releases on purpose to exceed the suppressed limit.
#pragma warning disable MEFF305
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException($"release {n}")));
#pragma warning restore MEFF305
        }

        await Task.Yield();
        return 1;
    });
}

[Collection(nameof(DiagnosticsCollection))]
public class CallerCancellationCallbackTests
{
    private const string MESSAGE = "scope callback failed on caller cancel";

    [Fact]
    public async Task RunExitAsync_CallerCancelsAndACallbackOnTheScopeTokenThrows_IsInterruptedAndReportsTheDefectOnce()
    {
        // Given
        var reported = Collect();
        using var caller = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            // Kept past the body: the body can end inline before this callback runs.
            scope.CancellationToken.Register(() => throw new InvalidOperationException(MESSAGE));
            ready.SetResult();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 42;
        });

        try
        {
            // When
            var running = work.RunExitAsync(EmptyEnvironment.Value, caller.Token).AsTask();
            await ready.Task;
            caller.Cancel();
            var exit = await running;

            // Then
            Assert.True(exit.TryGetValue(out Interrupted? _));
            Assert.Single(Ours(reported));
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task RunAsync_CallerCancelsAsyncAndACallbackOnTheScopeTokenThrows_ThrowsCanceledAndReportsTheDefectOnce()
    {
        // Given
        var reported = Collect();
        using var caller = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            // Kept past the body: the body can end inline before this callback runs.
            scope.CancellationToken.Register(() => throw new InvalidOperationException(MESSAGE));
            ready.SetResult();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 42;
        });

        try
        {
            // When
            var running = work.RunAsync(EmptyEnvironment.Value, caller.Token).AsTask();
            await ready.Task;
            await caller.CancelAsync();
            async Task Act() => await running;

            // Then
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Act);
            Assert.Single(Ours(reported));
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task RunExitAsync_CallbackThrowsAfterTheRunEndedOnTheCancelingThread_ReportsTheDefectOnce()
    {
        // Given
        var reported = Collect();
        using var caller = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource<int>();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            // Registered first and kept past the body, so it throws after the run has ended inline.
            scope.CancellationToken.Register(() => throw new InvalidOperationException(MESSAGE));
            scope.CancellationToken.Register(() => stop.TrySetResult(42));
            ready.SetResult();
            return await stop.Task.ConfigureAwait(false);
        });

        try
        {
            // When
            var running = work.RunExitAsync(EmptyEnvironment.Value, caller.Token).AsTask();
            await ready.Task;
            caller.Cancel();
            var exit = await running;

            // Then
            Assert.True(exit.TryGetValue(out Ok<int> ok));
            Assert.Equal(42, ok.Value);
            Assert.Single(Ours(reported));
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task RunExitAsync_CallbackOnTheCallersOwnTokenThrows_StillThrowsFromCancel()
    {
        // Given
        using var caller = new CancellationTokenSource();
        using var registration = caller.Token.Register(() => throw new InvalidOperationException(MESSAGE));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            ready.SetResult();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 42;
        });

        // When
        var running = work.RunExitAsync(EmptyEnvironment.Value, caller.Token).AsTask();
        await ready.Task;
        void Act() => caller.Cancel();

        // Then
        Assert.Throws<AggregateException>(Act);
        Assert.True((await running).TryGetValue(out Interrupted? _));
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
