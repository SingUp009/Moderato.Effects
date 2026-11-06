using System.Collections.Concurrent;

namespace Moderato.Effects.Tests;

[Collection(nameof(DiagnosticsCollection))]
public class RunOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_UnjoinedChildError_UsesTheRunsHandler(bool returnExit)
    {
        // Given
        var global = new ConcurrentQueue<ExitCase>();
        var reported = new ConcurrentQueue<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        Action<ExitCase> globalHandler = global.Enqueue;
        EffectDiagnostics.UnobservedFailureHandler = globalHandler;
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = UnjoinedError();

        try
        {
            // When
            if (returnExit)
                await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);
            else
                await effect.RunAsync(EmptyEnvironment.Value, CancellationToken.None, options);

            // Then
            Assert.Equal("child", Assert.IsType<Failed>(Assert.Single(reported).Value).Error);
            Assert.Empty(global);
            Assert.Same(globalHandler, EffectDiagnostics.UnobservedFailureHandler);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Fact]
    public async Task RunExitAsync_ConcurrentRunsOfOneDescription_KeepTheirHandlersSeparate()
    {
        // Given
        var first = new ConcurrentQueue<ExitCase>();
        var second = new ConcurrentQueue<ExitCase>();
        var effect = UnjoinedError();
        var firstOptions = new EffectRunOptions { UnobservedFailureHandler = first.Enqueue };
        var secondOptions = new EffectRunOptions { UnobservedFailureHandler = second.Enqueue };

        // When
        var exits = await Task.WhenAll(
            effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, firstOptions).AsTask(),
            effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, secondOptions).AsTask());

        // Then
        Assert.All(exits, exit => Assert.IsType<Ok<int>>(exit.Value));
        Assert.IsType<Failed>(Assert.Single(first).Value);
        Assert.IsType<Failed>(Assert.Single(second).Value);
    }

    [Fact]
    public async Task RunExitAsync_CustomAndLegacyRunsTogether_KeepTheirHandlersSeparate()
    {
        // Given
        var global = new ConcurrentQueue<ExitCase>();
        var local = new ConcurrentQueue<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = global.Enqueue;
        var options = new EffectRunOptions { UnobservedFailureHandler = local.Enqueue };
        var effect = UnjoinedError();

        try
        {
            // When
            await Task.WhenAll(
                effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options).AsTask(),
                effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask());

            // Then
            Assert.IsType<Failed>(Assert.Single(local).Value);
            Assert.IsType<Failed>(Assert.Single(global).Value);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_ProvidedEnvironmentOverload_UsesTheRunsHandler(bool useDefaultEnvironment, bool returnExit)
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var emptyEffect = UnjoinedError();
        var defaultEffect = emptyEffect.Provide<DefaultEnvironment>(_ => EmptyEnvironment.Value);
        Exit<int, Never> exit = default;
        Result<int, Never> result = default;

        // When
        if (useDefaultEnvironment)
        {
            if (returnExit) exit = await defaultEffect.RunExitAsync(CancellationToken.None, options);
            else result = await defaultEffect.RunAsync(CancellationToken.None, options);
        }
        else
        {
            if (returnExit) exit = await emptyEffect.RunExitAsync(CancellationToken.None, options);
            else result = await emptyEffect.RunAsync(CancellationToken.None, options);
        }

        // Then
        if (returnExit)
        {
            Assert.Equal(1, Assert.IsType<Ok<int>>(exit.Value).Value);
        }
        else
        {
            Assert.True(result.TryGetOk(out var value));
            Assert.Equal(1, value);
        }
        Assert.IsType<Failed>(Assert.Single(reported).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(1000)]
    public async Task RunExitAsync_ComposedRun_PropagatesTheHandlerThroughBothExecutors(int depth)
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = UnjoinedError();
        for (var index = 0; index < depth; index++) effect = effect.Map(static value => value + 1);

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);

        // Then
        Assert.Equal(depth + 1, Assert.IsType<Ok<int>>(exit.Value).Value);
        Assert.IsType<Failed>(Assert.Single(reported).Value);
    }

    [Fact]
    public async Task Timeout_ChildReports_PropagatesTheRunsHandler()
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = UnjoinedError().Provide<DefaultEnvironment>(_ => EmptyEnvironment.Value)
            .Timeout(Timeout.InfiniteTimeSpan);

        // When
        var exit = await effect.RunExitAsync(DefaultEnvironment.Value, CancellationToken.None, options);

        // Then
        Assert.Equal(1, Assert.IsType<Ok<int>>(exit.Value).Value);
        Assert.IsType<Failed>(Assert.Single(reported).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_ChildReports_UsesTheRunsHandler(bool parallel)
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var effect = Effect.All(new[] { UnjoinedError(), UnjoinedError() },
            parallel ? Concurrency.Parallel : Concurrency.Sequential);

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);

        // Then
        Assert.IsType<Ok<int[]>>(exit.Value);
        Assert.Equal(2, reported.Count);
        Assert.All(reported, item => Assert.IsType<Failed>(item.Value));
    }

    [Fact]
    public async Task Use_ReleaseReports_UsesTheRunsHandlerWithAnUncancelableToken()
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var releaseToken = new CancellationToken(canceled: true);
        var release = UnjoinedError().AndThen(_ => Effect<EmptyEnvironment>.Async(token =>
        {
            releaseToken = token;
            return new ValueTask<Unit>(Unit.Value);
        }));
        var effect = Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) => release)
            .Use(value => Effect<EmptyEnvironment>.Succeed(value));

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);

        // Then
        Assert.IsType<Ok<int>>(exit.Value);
        Assert.False(releaseToken.CanBeCanceled);
        Assert.IsType<Failed>(Assert.Single(reported).Value);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("result")]
    [InlineData("recover")]
    [InlineData("match")]
    [InlineData("fallback")]
    public async Task RunAsync_SuppressedDefectAtAResultBoundary_UsesTheRunsHandler(string boundary)
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var defect = new IOException("loser failed after cancellation");
        var failed = RacedFailure(defect);

        // When
        switch (boundary)
        {
            case "exit":
                await failed.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);
                break;
            case "result":
                await failed.RunAsync(EmptyEnvironment.Value, CancellationToken.None, options);
                break;
            case "recover":
                await failed.OrElse(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value))
                    .RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);
                break;
            case "match":
                await failed.Match(_ => Unit.Value, _ => Unit.Value)
                    .RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);
                break;
            default:
                await failed.UnwrapOr(Unit.Value)
                    .RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);
                break;
        }

        // Then
        Assert.Same(defect, Assert.IsType<Die>(Assert.Single(reported).Value).Exception);
    }

    [Fact]
    public async Task AllSettled_ChildProjectionReports_UsesTheRunsHandler()
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        var defect = new IOException("loser");
        var effect = Effect.AllSettled(new[] { RacedFailure(defect) });

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);

        // Then
        Assert.IsType<Ok<Exit<Unit, string>[]>>(exit.Value);
        Assert.Same(defect, Assert.IsType<Die>(Assert.Single(reported).Value).Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CancellationCallbackRunsOutsideTheExecution_UsesTheRunsHandler(bool returnExit)
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var options = new EffectRunOptions { UnobservedFailureHandler = reported.Enqueue };
        using var caller = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<int>();
        var defect = new IOException("callback");
        var effect = Effect<EmptyEnvironment>.Async<int>(token =>
        {
            token.UnsafeRegister(_ => throw defect, null);
            token.UnsafeRegister(_ => completed.TrySetResult(7), null);
            ready.SetResult();
            return new ValueTask<int>(completed.Task);
        });

        // When
        var exitTask = returnExit ? effect.RunExitAsync(EmptyEnvironment.Value, caller.Token, options).AsTask() : null;
        var resultTask = returnExit ? null : effect.RunAsync(EmptyEnvironment.Value, caller.Token, options).AsTask();
        await ready.Task;
        Task canceling;
        using (ExecutionContext.SuppressFlow()) canceling = Task.Run(caller.Cancel, TestContext.Current.CancellationToken);
        await canceling.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Exit<int, Never> exit = default;
        Result<int, Never> result = default;
        if (returnExit)
            exit = await exitTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        else
            result = await resultTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (returnExit)
        {
            Assert.Equal(7, Assert.IsType<Ok<int>>(exit.Value).Value);
        }
        else
        {
            Assert.True(result.TryGetOk(out var value));
            Assert.Equal(7, value);
        }
        Assert.Same(defect, Assert.IsType<Die>(Assert.Single(reported).Value).Exception);
    }

    [Fact]
    public async Task RunExitAsync_HandlerThrows_PreservesTheResult()
    {
        // Given
        var calls = 0;
        var options = new EffectRunOptions
        {
            UnobservedFailureHandler = _ => { calls++; throw new IOException("diagnostics failed"); },
        };
        var effect = UnjoinedError();

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, options);

        // Then
        Assert.Equal(1, calls);
        Assert.IsType<Ok<int>>(exit.Value);
    }

    [Fact]
    public async Task RunExitAsync_NoRunHandler_UsesTheGlobalHandler()
    {
        // Given
        var reported = new ConcurrentQueue<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = reported.Enqueue;
        var effect = UnjoinedError();

        try
        {
            // When
            await effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, new EffectRunOptions());

            // Then
            Assert.IsType<Failed>(Assert.Single(reported).Value);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Fact]
    public void RunExitAsync_NullOptions_ThrowsArgumentNullException()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed(1);

        // When
        var exception = Assert.Throws<ArgumentNullException>(() =>
            effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None, null!));

        // Then
        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task RunExitAsync_LegacyDefaultTokenCall_RemainsUnambiguous()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed(1);

        // When
#pragma warning disable xUnit1051 // An explicit default token verifies source compatibility with the original overload.
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, default);
#pragma warning restore xUnit1051

        // Then
        Assert.Equal(1, Assert.IsType<Ok<int>>(exit.Value).Value);
    }

    private static Effect<int, Never, EmptyEnvironment> UnjoinedError()
        => Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await scope.Fork(Effect<EmptyEnvironment>.Fail("child").TapErr(_ => ended.TrySetResult()));
            await ended.Task;
            return 1;
        });

    private static Effect<Unit, string, EmptyEnvironment> RacedFailure(Exception defect)
        => Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Effect<Unit, string, EmptyEnvironment> loser = Effect<EmptyEnvironment>.Async<Unit>(async token =>
            {
                entered.SetResult();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, TestContext.Current.CancellationToken);
                try { await Task.Delay(Timeout.Infinite, linked.Token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw defect; }
                return Unit.Value;
            });
            var winner = Scope<string, EmptyEnvironment>.Gen<Unit>(async child =>
            {
                await entered.Task;
                throw child.Fail("winner");
            });
            return await scope.Run(Effect.RaceFirst(winner, loser));
        });
}
