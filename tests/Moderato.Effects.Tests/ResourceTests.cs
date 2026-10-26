namespace Moderato.Effects.Tests;

public sealed record Connection(string Name);

public class ResourceTests
{
    private static Effect<Unit, Never, EmptyEnvironment> Record(List<string> log, string entry)
        => Effect<EmptyEnvironment>.Sync(() =>
        {
            log.Add(entry);
            return Unit.Value;
        });

    private static Resource<Connection, AppError, EmptyEnvironment> Open(List<string> log, string name)
        => Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Sync(() =>
            {
                log.Add($"open {name}");
                return new Connection(name);
            }),
            release: (connection, exit) => Record(log, $"close {connection.Name} ({exit.Value?.GetType().Name})"));

    private static Resource<Connection, AppError, EmptyEnvironment> Refused(int id)
        => Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(id)),
            release: (_, _) => throw new InvalidOperationException("never acquired, never released"));

    [Fact]
    public async Task Use_UseSucceeds_ReleasesWithSucceeded()
    {
        // Given
        var log = new List<string>();
        var db = Open(log, "db");

        // When
        var result = await db.Use(connection => Effect<EmptyEnvironment>.Succeed(connection.Name.Length)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Ok(2), result);
        Assert.Equal(["open db", "close db (Succeeded)"], log);
    }

    [Fact]
    public async Task Use_UseFails_ReleasesWithFailedAndKeepsTheErr()
    {
        // Given
        var log = new List<string>();
        var db = Open(log, "db");

        // When
        var result = await db.Use(_ => (Effect<int, AppError, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("read only")))
            .RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new Forbidden("read only")), result);
        Assert.Equal(["open db", "close db (Failed)"], log);
    }

    [Fact]
    public async Task Use_UseThrows_ReleasesWithDieAndDies()
    {
        // Given
        var log = new List<string>();
        var db = Open(log, "db");

        // When
        var exit = await db.Use<int>(_ => throw new InvalidOperationException("bug")).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal(["open db", "close db (Die)"], log);
    }

    [Fact]
    public async Task Use_AcquireFails_DoesNotRunTheUseOrTheRelease()
    {
        // Given
        var used = false;
        var db = Refused(404);

        // When
        var result = await db.Use(_ =>
        {
            used = true;
            return Effect<EmptyEnvironment>.Succeed(1);
        }).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Err<AppError>(new NotFound(404)), result);
        Assert.False(used);
    }

    [Fact]
    public async Task Use_RunTwice_AcquiresAndReleasesEachTime()
    {
        // Given
        var log = new List<string>();
        var count = Open(log, "db").Use(_ => Effect<EmptyEnvironment>.Succeed(1));

        // When
        await count.RunAsync(TestContext.Current.CancellationToken);
        await count.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(["open db", "close db (Succeeded)", "open db", "close db (Succeeded)"], log);
    }

    [Fact]
    public async Task Use_ReleaseThrows_DiesKeepingTheUsesErrAsSuppressed()
    {
        // Given
        var db = Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Succeed(new Connection("db")),
            release: (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("rollback failed")));

        // When
        var exit = await db.Use(_ => (Effect<int, AppError, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("read only")))
            .RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
        Assert.Equal([new Suppressed(new Failed(new Forbidden("read only")), Relation.Sequential)], die.Suppressed);
    }

    [Fact]
    public async Task Use_CallerCancelsRightAfterTheAcquire_StillReleases()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var db = Resource.Make(
            acquire: Effect<EmptyEnvironment>.Sync(() =>
            {
                cts.Cancel();
                return new Connection("db");
            }),
            release: (connection, exit) => Record(log, $"close {connection.Name} ({exit.Value?.GetType().Name})"));

        // When
        var exit = await db.Use(_ => Effect<EmptyEnvironment>.Async(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(1);
        })).RunExitAsync(EmptyEnvironment.Value, cts.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(["close db (Interrupted)"], log);
    }

    [Fact]
    public async Task Use_CallerCanceled_ReleasesWithAnUncanceledToken()
    {
        // Given
        using var cts = new CancellationTokenSource();
        CancellationToken releaseToken = default;
        var db = Resource.Make(
            acquire: Effect<EmptyEnvironment>.Succeed(new Connection("db")),
            release: (_, _) => Effect<EmptyEnvironment>.Async(ct =>
            {
                releaseToken = ct;
                return ValueTask.FromResult(Unit.Value);
            }));

        // When
        await db.Use(_ => Effect<EmptyEnvironment>.Async(async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return 1;
        })).RunExitAsync(EmptyEnvironment.Value, cts.Token);

        // Then
        Assert.False(releaseToken.IsCancellationRequested);
    }

    [Fact]
    public void Use_DefaultResource_Throws()
    {
        // Given
        Resource<Connection, AppError, EmptyEnvironment> db = default;

        // When
        void Act() => db.Use(_ => Effect<EmptyEnvironment>.Succeed(1));

        // Then
        Assert.Throws<InvalidOperationException>(Act);
    }

    [Fact]
    public async Task Map_TransformsTheValue_ReleasesTheOriginal()
    {
        // Given
        var log = new List<string>();
        var name = Open(log, "db").Map(connection => connection.Name.ToUpperInvariant());

        // When
        var result = await name.Use(n => Effect<EmptyEnvironment>.Succeed(n)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Ok("DB"), result);
        Assert.Equal(["open db", "close db (Succeeded)"], log);
    }

    [Fact]
    public async Task MapErr_AcquireFails_TransformsTheError()
    {
        // Given
        var db = Refused(404).MapErr(error => error.Value is NotFound n ? $"no database {n.Id}" : "unknown");

        // When
        var result = await db.Use(connection => Effect<EmptyEnvironment>.Succeed(connection.Name)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, string>>(Result.Err("no database 404"), result);
    }

    [Fact]
    public async Task MapErr_AcquireSucceeds_KeepsTheValueAndReleasesIt()
    {
        // Given
        var log = new List<string>();
        var db = Open(log, "db").MapErr(_ => "unused");

        // When
        var result = await db.Use(connection => Effect<EmptyEnvironment>.Succeed(connection.Name)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, string>>(Result.Ok("db"), result);
        Assert.Equal(["open db", "close db (Succeeded)"], log);
    }

    [Fact]
    public async Task MapErr_MapperThrows_Dies()
    {
        // Given
        var db = Refused(404).MapErr<string>(_ => throw new InvalidOperationException("bug"));

        // When
        var exit = await db.Use(connection => Effect<EmptyEnvironment>.Succeed(connection.Name)).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
    }

    [Fact]
    public async Task MapErr_MapperThrows_DiesKeepingTheOriginalErr()
    {
        // Given
        var db = Refused(404).MapErr<string>(_ => throw new InvalidOperationException("bug"));

        // When
        var exit = await db.Use(connection => Effect<EmptyEnvironment>.Succeed(connection.Name)).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        var kept = Assert.Single(die.Suppressed);
        Assert.True(kept.Case is Failed(NotFound { Id: 404 }));
    }

    [Fact]
    public async Task Use_FactoryThrowsAfterAnAcquisitionWithASuppressedDefect_KeepsItAndReleases()
    {
        // Given
        var released = false;
        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, Never, EmptyEnvironment> winner = Effect<EmptyEnvironment>.Async(async _ =>
        {
            await loserStarted.Task;
            return 1;
        });
        Effect<int, Never, EmptyEnvironment> loser = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            try
            {
                loserStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("loser failed");
            }

            return 2;
        });
        var resource = Resource.Make(Effect.Race(winner, loser), (_, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            released = true;
            return Unit.Value;
        }));

        // When
        var exit = await resource.Use<int>(_ => throw new FormatException("factory failed")).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("factory failed", die.Exception.Message);
        Assert.Contains(die.Suppressed, s => s.Case.Value is Die d && d.Exception.Message == "loser failed");
        Assert.True(released);
    }
}

public class ResourceLinqTests
{
    private static Resource<Connection, AppError, EmptyEnvironment> Open(List<string> log, string name)
        => Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Sync(() =>
            {
                log.Add($"open {name}");
                return new Connection(name);
            }),
            release: (connection, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                log.Add($"close {connection.Name}");
                return Unit.Value;
            }));

    [Fact]
    public async Task SelectMany_TwoResources_ReleasesInReverseOrder()
    {
        // Given
        var log = new List<string>();

        // When
        var both = from db in Open(log, "db")
                   from tx in Open(log, $"tx on {db.Name}")
                   select (db, tx);
        await both.Use(pair => Effect<EmptyEnvironment>.Succeed(pair.tx.Name)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(["open db", "open tx on db", "close tx on db", "close db"], log);
    }

    [Fact]
    public async Task SelectMany_SecondAcquireFails_ReleasesTheFirst()
    {
        // Given
        var log = new List<string>();
        var refused = Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("no tx")),
            release: (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));

        // When
        var both = from db in Open(log, "db")
                   from tx in refused
                   select tx;
        var result = await both.Use(tx => Effect<EmptyEnvironment>.Succeed(tx.Name)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Err<AppError>(new Forbidden("no tx")), result);
        Assert.Equal(["open db", "close db"], log);
    }

    [Fact]
    public async Task SelectMany_OneReleaseThrows_TheOtherReleaseSeesTheSameExit()
    {
        // Given
        var seen = new List<string>();
        var db = Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Succeed(new Connection("db")),
            release: (_, exit) => Effect<EmptyEnvironment>.Sync(() =>
            {
                seen.Add(exit is Succeeded ? "db sees success" : "db sees failure");
                return Unit.Value;
            }));
        var tx = Resource.Make<Connection, AppError, EmptyEnvironment>(
            acquire: Effect<EmptyEnvironment>.Succeed(new Connection("tx")),
            release: (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("commit failed")));

        // When
        var both = from d in db from t in tx select t;
        var exit = await both.Use(t => Effect<EmptyEnvironment>.Succeed(t.Name)).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(["db sees success"], seen);
    }

    [Fact]
    public async Task SelectMany_ResourceThatCannotFail_MixesWithOneThatCan()
    {
        // Given
        var log = new List<string>();
        var buffer = Resource.Make(
            acquire: Effect<EmptyEnvironment>.Succeed(new byte[16]),
            release: (_, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                log.Add("return buffer");
                return Unit.Value;
            }));

        // When
        var both = from b in buffer
                   from db in Open(log, "db")
                   select (b.Length, db.Name);
        var result = await both.Use(pair => Effect<EmptyEnvironment>.Succeed(pair)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<(int, string), AppError>>(Result.Ok((16, "db")), result);
        Assert.Equal(["open db", "close db", "return buffer"], log);
    }
}

public class AcquireTests
{
    [Fact]
    public async Task Run_NotAwaitedAndFailingAfterTheBlock_EndsTheBlockWithItsErr()
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> slow = Effect<EmptyEnvironment>.Async(async _ =>
        {
            await gate.Task;
            return 1;
        });
        var failsLate = slow.AndThen<int>(_ => Effect<EmptyEnvironment>.Fail("late failure"));
        var work = Scope<string, EmptyEnvironment>.Gen<int>(scope =>
        {
            // Not awaited on purpose: the step fails only after the block has returned.
#pragma warning disable MEFF310
            _ = scope.Run(failsLate);
#pragma warning restore MEFF310
            bodyEnded.SetResult();
            return ValueTask.FromResult(0);
        });

        // When
        var running = work.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        await bodyEnded.Task;
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.Equal<Exit<int, string>>(new Err<string>("late failure"), exit);
    }

    [Fact]
    public async Task Acquire_NotAwaitedAndFinishingAfterTheBlock_IsStillReleased()
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        var resource = Resource.Make(
            Effect<EmptyEnvironment>.Async<int>(async _ =>
            {
                await gate.Task;
                return 1;
            }),
            (_, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                released = true;
                return Unit.Value;
            }));
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            // Not awaited on purpose: the acquisition finishes only after the block has returned.
#pragma warning disable MEFF310
            _ = scope.Acquire(resource);
#pragma warning restore MEFF310
            bodyEnded.SetResult();
            return ValueTask.FromResult(0);
        });

        // When
        var running = work.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        await bodyEnded.Task;
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.Equal<Exit<int, Never>>(new Ok<int>(0), exit);
        Assert.True(released);
    }

    [Fact]
    public async Task Acquire_NotAwaitedAndWaitingForCancellation_IsCanceledWhenTheBlockEnds()
    {
        // Given
        var stopped = false;
        var resource = Resource.Make(
            Effect<EmptyEnvironment>.Async<int>(async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    stopped = true;
                }

                return 1;
            }),
            (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            // Not awaited on purpose: the acquisition would otherwise outlive the block.
#pragma warning disable MEFF310
            _ = scope.Acquire(resource);
#pragma warning restore MEFF310
            return ValueTask.FromResult(0);
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Exit<int, Never>>(new Ok<int>(0), exit);
        Assert.True(stopped);
    }

    private static Resource<Connection, NotFound, EmptyEnvironment> Open(List<string> log, string name, bool exists = true)
        => Resource.Make(
            acquire: exists
                ? Effect<EmptyEnvironment>.Succeed(new Connection(name))
                : (Effect<Connection, NotFound, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail(new NotFound(0)),
            release: (connection, exit) => Effect<EmptyEnvironment>.Sync(() =>
            {
                log.Add($"close {connection.Name} ({exit.Value?.GetType().Name})");
                return Unit.Value;
            }));

    [Fact]
    public async Task Acquire_InGen_ReleasesWhenTheBlockEnds()
    {
        // Given
        var log = new List<string>();
        var work = Scope<NotFound, EmptyEnvironment>.Gen(async scope =>
        {
            var db = await scope.Acquire(Open(log, "db"));
            log.Add($"query {db.Name}");
            return db.Name;
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, NotFound>>(Result.Ok("db"), result);
        Assert.Equal(["query db", "close db (Succeeded)"], log);
    }

    [Fact]
    public async Task Acquire_WithDefer_ReleasesEverythingInReverseOrder()
    {
        // Given
        var log = new List<string>();
        var work = Scope<NotFound, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(Open(log, "db"));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() =>
            {
                log.Add("flush log");
                return Unit.Value;
            }));
            await scope.Acquire(Open(log, "cache"));
            return 1;
        });

        // When
        await work.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(["close cache (Succeeded)", "flush log", "close db (Succeeded)"], log);
    }

    [Fact]
    public async Task Acquire_AcquireFails_EndsTheBlockAndReleasesWhatWasAcquired()
    {
        // Given
        var log = new List<string>();
        var reachedTheEnd = false;
        var work = Scope<NotFound, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(Open(log, "db"));
            await scope.Acquire(Open(log, "cache", exists: false));
            reachedTheEnd = true;
            return 1;
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, NotFound>>(Result.Err(new NotFound(0)), result);
        Assert.False(reachedTheEnd);
        Assert.Equal(["close db (Failed)"], log);
    }

    [Fact]
    public async Task Acquire_ResourceWithACaseOfTheUnion_IsAcquiredInTheUnionsScope()
    {
        // Given
        var log = new List<string>();
        var work = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var db = await scope.Acquire(Open(log, "db", exists: false));
            return db.Name;
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, AppError>>(Result.Err<AppError>(new NotFound(0)), result);
    }

    [Fact]
    public async Task Acquire_ResourceThatCannotFail_IsAcquiredInAnyScope()
    {
        // Given
        var buffer = Resource.Make(
            acquire: Effect<EmptyEnvironment>.Succeed(new byte[16]),
            release: (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var work = Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
            var bytes = await scope.Acquire(buffer);
            return bytes.Length;
        });

        // When
        var result = await work.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Ok(16), result);
    }
}

[Collection(nameof(DiagnosticsCollection))]
public class AbandonedStepCallbackTests
{
    private const string MESSAGE = "scope callback failed while the block closed";

    [Fact]
    public async Task Run_NotAwaitedWhenTheBlockEnds_ReportsATokenCallbackThatThrows()
    {
        // Given
        var reported = new List<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); };
        Effect<int, Never, EmptyEnvironment> forever = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.CancellationToken.Register(() => throw new InvalidOperationException(MESSAGE));
            // Not awaited on purpose: the block cancels it when it ends.
#pragma warning disable MEFF310
            _ = scope.Run(forever);
#pragma warning restore MEFF310
            return ValueTask.FromResult(0);
        });

        try
        {
            // When
            var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal<Exit<int, Never>>(new Ok<int>(0), exit);
            List<Die> ours;
            lock (reported) ours = [.. reported.Select(static f => f.Value).OfType<Die>().Where(static d => d.Exception.Message == MESSAGE)];
            Assert.Single(ours);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }
}

[Collection(nameof(DiagnosticsCollection))]
public class ReleaseSuppressedTests
{
    private const string MESSAGE = "race loser failed during release";

    // A release that succeeds while its losing branch dies: the Race is Ok, the loser's defect is suppressed.
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
    public async Task Defer_ReleaseSucceedsWithASuppressedDefect_ReportsTheDefect()
    {
        // Given
        var reported = Collect();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => RaceWithDyingLoser());
            await Task.Yield();
            return 1;
        });

        try
        {
            // When
            var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal<Exit<int, Never>>(new Ok<int>(1), exit);
            Assert.Single(Ours(reported));
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }

    [Fact]
    public async Task Use_ReleaseSucceedsWithASuppressedDefectAfterTheBodyDies_KeepsItOnTheDie()
    {
        // Given
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) => RaceWithDyingLoser());
        Effect<int, Never, EmptyEnvironment> broken = Effect<EmptyEnvironment>.Sync<int>(() => throw new FormatException("body failed"));

        // When
        var exit = await resource.Use(_ => broken).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<FormatException>(die.Exception);
        Assert.Contains(die.Suppressed, s => s.Case.Value is Die d && d.Exception.Message == MESSAGE);
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
