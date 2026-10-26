namespace Moderato.Effects.Tests;

public class GenTests
{
    private static Effect<User, AppError, EmptyEnvironment> FindUser(int id)
        => id > 0 ? Effect<EmptyEnvironment>.Succeed(new User(id, $"user{id}")) : Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(id));

    [Fact]
    public async Task Gen_EveryStepSucceeds_RunsToTheBlocksValue()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var user = await scope.Run(FindUser(1));
            return user.Name;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var name));
        Assert.Equal("user1", name);
    }

    [Fact]
    public async Task Gen_ParameterTypeWritten_InfersTheEffectType()
    {
        // Given
        var effect = Effect.Gen(async (Scope<AppError, EmptyEnvironment> scope) =>
        {
            var user = await scope.Run(FindUser(2));
            return user.Id;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var id));
        Assert.Equal(2, id);
    }

    [Fact]
    public async Task Gen_RunTwice_RunsTheBlockEachTime()
    {
        // Given
        var calls = 0;
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            return ++calls;
        });

        // When
        await effect.RunAsync(TestContext.Current.CancellationToken);
        var second = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(2), second);
    }

    [Fact]
    public async Task Gen_BlockThrows_RunExitAsyncReturnsDie()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await Task.Yield();
            throw new InvalidOperationException("bug");
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
    }

    [Fact]
    public async Task Gen_CallerCancels_RunExitAsyncReturnsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 0;
        });

        // When
        var exit = await effect.RunExitAsync(cts.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task Gen_NestedGenFails_EndsTheOuterBlockWithTheSameErr()
    {
        // Given
        var steps = new List<string>();
        var inner = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var user = await scope.Run(FindUser(0));
            return user.Name;
        });
        var outer = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var name = await scope.Run(inner);
            steps.Add("after inner");
            return name;
        });

        // When
        var result = await outer.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(0), error.Value);
        Assert.Empty(steps);
    }
}

public class ScopeRunTests
{
    private static Effect<User, AppError, EmptyEnvironment> FindUser(int id)
        => id > 0 ? Effect<EmptyEnvironment>.Succeed(new User(id, $"user{id}")) : Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(id));

    [Fact]
    public async Task Run_StepFails_EndsTheBlockWithItsErr()
    {
        // Given
        var steps = new List<string>();
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var user = await scope.Run(FindUser(0));
            steps.Add("unreachable");
            return user;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(0), error.Value);
        Assert.Empty(steps);
    }

    [Fact]
    public async Task Run_EffectsOfOtherShapes_AreAcceptedAsTheyAre()
    {
        // Given
        Effect<User, NotFound, EmptyEnvironment> findCase = Effect<EmptyEnvironment>.Succeed(new User(3, "case"));
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var cannotFail = await scope.Run(Effect<EmptyEnvironment>.Sync(() => 1));
            var fromCase = await scope.Run(findCase);
            var parsed = await scope.Run(Effect<EmptyEnvironment>.Try(() => int.Parse("2"), (FormatException ex) => (AppError)new Forbidden(ex.Message)));
            var attempted = await scope.Run(FindUser(0).Attempt());
            return cannotFail + fromCase.Id + parsed + (attempted.IsErr ? 10 : 0);
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var total));
        Assert.Equal(1 + 3 + 2 + 10, total);
    }

    [Fact]
    public async Task Run_EffectThatCannotSucceed_EndsTheBlockWithItsErr()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Run(Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("closed")));
            return 1;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new Forbidden("closed"), error.Value);
    }

    [Fact]
    public async Task Run_StepThrows_RunsTheBlocksFinallyAndEndsWithDie()
    {
        // Given
        var steps = new List<string>();
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            try
            {
                return await scope.Run(Effect<EmptyEnvironment>.Sync<int>(() => throw new InvalidOperationException("bug")));
            }
            finally
            {
                steps.Add("finally");
            }
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(["finally"], steps);
    }

    [Fact]
    public async Task Run_StepFailsInsideUsing_DisposesBeforeTheBlockEnds()
    {
        // Given
        var resource = new Disposable();
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            using var _ = resource;
            return await scope.Run(FindUser(0));
        });

        // When
        await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task Run_EnvironmentReadInsideTheBlock_ResolvesTheService()
    {
        // Given
        var counter = new Counter();
        var effect = Scope<Never, CounterEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            return scope.Env.Resolve<Counter>().Next();
        });

        // When
        var result = await effect.RunAsync(new CounterEnvironment(counter), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
    }

    private sealed class Disposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

public class ScopeGetTests
{
    [Fact]
    public async Task Get_OkResults_ReturnsTheirValues()
    {
        // Given
        Result<int, AppError> validated = Result.Ok(1);
        Result<int, NotFound> found = Result.Ok(2);
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            return scope.Get(validated) + scope.Get(found);
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var sum));
        Assert.Equal(3, sum);
    }

    [Fact]
    public async Task Get_ErrOfACase_EndsTheBlockWithTheUnion()
    {
        // Given
        Result<int, NotFound> found = Result.Err(new NotFound(5));
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            return scope.Get(found);
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(5), error.Value);
    }

    [Fact]
    public async Task Get_DefaultResult_EndsTheBlockWithDie()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            return scope.Get(default(Result<int, AppError>));
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
    }
}

public class ScopeFailTests
{
    [Fact]
    public async Task Fail_ThrownForAMissingValue_EndsTheBlockAndNarrowsTheValue()
    {
        // Given
        User? missing = null;
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            if (missing is null) throw scope.Fail(new NotFound(4));
            return missing.Name;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(4), error.Value);
    }

    [Fact]
    public async Task Fail_WithoutThrow_StillEndsTheBlock()
    {
        // Given
        var steps = new List<string>();
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            scope.Fail(new Forbidden("inactive"));
            steps.Add("unreachable");
            return 1;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new Forbidden("inactive"), error.Value);
        Assert.Empty(steps);
    }
}

public class SwallowedErrTests
{
    [Fact]
    public async Task Run_AfterACatchSwallowedTheErr_DoesNotRunTheStep()
    {
        // Given
        var commits = 0;
        var commit = Effect<EmptyEnvironment>.Sync(() => ++commits);
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            // These tests swallow ScopeFailure on purpose to show the runtime still keeps the Err.
#pragma warning disable MEFF301
            try
            {
                throw scope.Fail(new Forbidden("invalid order"));
            }
            catch (Exception)
            {
            }
#pragma warning restore MEFF301

            return await scope.Run(commit);
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new Forbidden("invalid order"), error.Value);
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task Gen_CatchSwallowedTheErrAndTheBlockReturns_StillEndsWithTheErr()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            // These tests swallow ScopeFailure on purpose to show the runtime still keeps the Err.
#pragma warning disable MEFF301
            try
            {
                scope.Fail(new NotFound(1));
            }
            catch (Exception)
            {
            }
#pragma warning restore MEFF301

            return "looks fine";
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(1), error.Value);
    }

    [Fact]
    public async Task Gen_CatchThrowsAnotherException_EndsWithDieKeepingTheErr()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await Task.Yield();
            try
            {
                throw scope.Fail(new NotFound(1));
            }
            catch (ScopeFailure)
            {
                throw new InvalidOperationException("wrapped");
            }
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        var kept = Assert.Single(die.Suppressed);
        Assert.Equal(new Suppressed(new Failed(new NotFound(1)), Relation.Sequential), kept);
    }

    [Fact]
    public async Task Run_InsideFinallyAfterAnErr_DoesNotRunTheStep()
    {
        // Given
        var rollbacks = 0;
        var rollback = Effect<EmptyEnvironment>.Sync(() => ++rollbacks);
        var effect = Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            try
            {
                throw scope.Fail(new Forbidden("invalid order"));
            }
            finally
            {
                // Shows that a step in a finally block does not run after an Err.
#pragma warning disable MEFF302
                await scope.Run(rollback);
#pragma warning restore MEFF302
            }
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out _));
        Assert.Equal(0, rollbacks);
    }
}

public class DeferTests
{
    private static Effect<Unit, Never, EmptyEnvironment> Record(List<string> log, string entry)
        => Effect<EmptyEnvironment>.Sync(() => { log.Add(entry); return Unit.Value; });

    [Fact]
    public async Task Defer_BlockSucceeds_RunsTheReleaseWithSucceeded()
    {
        // Given
        var log = new List<string>();
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            scope.Defer(exit => Record(log, exit is Succeeded ? "commit" : "rollback"));
            return 1;
        });

        // When
        await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(["commit"], log);
    }

    [Fact]
    public async Task Defer_BlockFails_RunsTheReleaseWithFailed()
    {
        // Given
        var log = new List<string>();
        var effect = Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await Task.Yield();
            scope.Defer(exit => Record(log, exit is Succeeded ? "commit" : "rollback"));
            throw scope.Fail(new Forbidden("invalid order"));
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out _));
        Assert.Equal(["rollback"], log);
    }

    [Fact]
    public async Task Defer_SeveralReleases_RunInReverseOrder()
    {
        // Given
        var log = new List<string>();
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            scope.Defer(_ => Record(log, "close connection"));
            scope.Defer(_ => Record(log, "end transaction"));
            return 1;
        });

        // When
        await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(["end transaction", "close connection"], log);
    }

    [Fact]
    public async Task Defer_CallerCanceled_RunsTheReleaseWithAnUncanceledToken()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var releaseToken = new CancellationToken(canceled: true);
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Async(ct => { releaseToken = ct; return ValueTask.FromResult(Unit.Value); }));
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, scope.CancellationToken);
            return 1;
        });

        // When
        var exit = await effect.RunExitAsync(cts.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.False(releaseToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Defer_ReleaseThrows_EndsWithDieKeepingTheBlocksErr()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await Task.Yield();
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("disk gone")));
            throw scope.Fail(new Forbidden("invalid order"));
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
        var kept = Assert.Single(die.Suppressed);
        Assert.True(kept.Case is Failed(Forbidden { Reason: "invalid order" }));
    }

    [Fact]
    public async Task Defer_OneReleaseThrows_TheOthersStillRunAndSeeTheSameExit()
    {
        // Given
        var seen = new List<string>();
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            scope.Defer(exit => Record(seen, exit is Succeeded ? "first sees success" : "first sees failure"));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("disk gone")));
            return 1;
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(["first sees success"], seen);
    }
}

public class DeferredReleaseFailureTests
{
    [Fact]
    public async Task Defer_ReleaseFailsAndSoDoesItsOwnRelease_KeepsBothDefects()
    {
        // Given
        var release = Scope<Never, EmptyEnvironment>.Gen<Unit>(async inner =>
        {
            inner.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("inner release failed")));
            await Task.Yield();
            throw new InvalidOperationException("release failed");
        });
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => release);
            await Task.Yield();
            return 1;
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("release failed", die.Exception.Message);
        var kept = Assert.Single(die.Suppressed);
        Assert.True(kept.Case is Die(IOException { Message: "inner release failed" }));
    }
}

public class ScopeMisuseTests
{
    [Fact]
    public async Task Run_AfterTheBlockEnded_Throws()
    {
        // Given
        Scope<Never, EmptyEnvironment>? escaped = null;
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            escaped = scope;
            return 1;
        });
        await effect.RunAsync(TestContext.Current.CancellationToken);

        // When
        var act = async () => await escaped!.Run(Effect<EmptyEnvironment>.Succeed(2));

        // Then
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task Run_FromTwoFlowsAtOnce_EndsTheBlockWithDie()
    {
        // Given
        var slow = Effect<EmptyEnvironment>.Async(async ct =>
        {
            await Task.Delay(50, ct);
            return 1;
        });
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var both = await Task.WhenAll(scope.Run(slow).AsTask(), scope.Run(slow).AsTask());
            return both.Sum();
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
    }

    [Fact]
    public async Task Fail_OnTheOuterScopeInsideANestedGen_EndsWithDie()
    {
        // Given
        var effect = Scope<AppError, EmptyEnvironment>.Gen(async outer =>
        {
            return await outer.Run(Scope<Never, EmptyEnvironment>.Gen<int>(async inner =>
            {
                await Task.Yield();
                // Shows that the runtime rejects the outer scope inside a nested Gen block.
#pragma warning disable MEFF303
                throw outer.Fail(new Forbidden("from inside"));
#pragma warning restore MEFF303
            }));
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
    }
}
