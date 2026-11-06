using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class TransactionTests
{
    [Fact]
    public async Task Transaction_Success_CommitsAndDisposesTheContext()
    {
        // Given
        await using var store = await Store.Open();

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env,
            environment: (_, db) => db,
            body: Insert(1)).RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Ok<int> ok));
        Assert.Equal(1, ok.Value);
        Assert.Equal(1, await store.Count());
        Assert.Equal(1, store.Probe.Commits);
        Assert.Equal(0, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Theory]
    [InlineData("Err")]
    [InlineData("Die")]
    [InlineData("Interrupted")]
    [InlineData("Release")]
    public async Task Transaction_BodyFails_RollsBackBeforeDisposal(string kind)
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("body");
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "staged" });
            await scope.Env.SaveChangesAsync(scope.CancellationToken);
            if (kind == "Err") throw scope.Fail(new Rejected("body"));
            if (kind == "Die") throw failure;
            if (kind == "Interrupted")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(scope.CancellationToken);
            }
            scope.Defer(_ => Effect<TestDb>.Sync<Unit>(() => throw failure));
            return 1;
        });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        if (kind == "Err")
        {
            Assert.True(exit.TryGetValue(out Err<TestError> _));
        }
        else if (kind == "Interrupted")
        {
            Assert.True(exit.TryGetValue(out Interrupted? _));
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(failure, die.Exception);
        }
        Assert.Equal(0, await store.Count());
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.False(store.Probe.RollbackCanBeCanceled);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_UnconfirmedCommitWithoutApplicationMapping_ReturnsStandardErr(bool provideMapper)
    {
        // Given
        await using var store = await Store.Open();
        var failure = new IOException("reply unavailable");
        store.Probe.BeforeCommit = _ => throw failure;
        TryMapDatabaseFailure<TestError>? mapper = provideMapper ? NoMapping : null;

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1), commitFailure: mapper)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        var unknown = Assert.IsType<CommitNotConfirmed>(err.Error.Value);
        Assert.Same(failure, unknown.Cause);
        Assert.Equal(0, await store.Count());
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CanceledDuringCommit_ReturnsStandardErr()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        store.Probe.BeforeCommit = token =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        };

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1))
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        Assert.IsType<CommitNotConfirmed>(err.Error.Value);
        Assert.Equal(0, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CanceledBeforeCommit_DoesNotCallCommit()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        var body = Insert(1).Map(value => { cancellation.Cancel(); return value; });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_CanceledAfterSuccessfulCommit_KeepsTheSuccess()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        store.Probe.AfterCommit = () => cancellation.Cancel();

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1))
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Ok<int> _));
        Assert.Equal(1, await store.Count());
        Assert.Equal(0, store.Probe.Rollbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_DeferredForeignKeyRejected_ClassifiesTheConfirmedFailure(bool map)
    {
        // Given
        await using var store = await Store.Open();
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            await scope.Env.Database.ExecuteSqlRawAsync("INSERT INTO Children VALUES (1, 999)", scope.CancellationToken);
            return 1;
        });
        TryMapDatabaseFailure<TestError>? mapper = map ? MapForeignKey : null;

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: mapper)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        if (map)
        {
            Assert.True(exit.TryGetValue(out Err<TestError> err));
            Assert.IsType<Rejected>(err.Error.Value);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.IsType<SqliteException>(die.Exception);
        }
        Assert.Equal(0, await store.Count("Children"));
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_RollbackFails_StillDisposesAndRetainsTheBodyErr()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("rollback");
        store.Probe.RollbackFailure = failure;
        Effect<int, TestError, TestDb> body = Scope<TestError, TestDb>.Gen<int>(scope =>
            throw scope.Fail(new Rejected("body")));

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed && item.Relation == Relation.Sequential);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_DisposeFailsAfterCommit_DoesNotRollback()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("dispose");
        store.Probe.DisposeFailure = failure;

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1))
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(1, await store.Count());
        Assert.Equal(0, store.Probe.Rollbacks);
    }

    [Fact]
    public async Task Transaction_Reexecuted_CreatesFreshContexts()
    {
        // Given
        await using var store = await Store.Open();
        Effect<Guid, TestError, TestDb> body = Effect<TestDb>.Access(db => db.Instance);
        var effect = EfDatabase<Store>.Transaction(contexts: env => env,
            environment: (_, db) => db, body: body);

        // When
        var first = await effect.RunExitAsync(store, TestContext.Current.CancellationToken);
        var second = await effect.RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(first.TryGetValue(out Ok<Guid> one));
        Assert.True(second.TryGetValue(out Ok<Guid> two));
        Assert.NotEqual(one.Value, two.Value);
        Assert.Equal(2, store.Probe.Disposals);
    }

    private static Effect<int, TestError, TestDb> Insert(int id)
        => Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = id, Name = "saved" });
            return await scope.Env.SaveChangesAsync(scope.CancellationToken);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsync_DuplicateKey_MapsOnlyWhenRequested(bool map)
    {
        // Given
        await using var store = await Store.Open();
        await using (var existing = store.CreateDbContext())
        {
            existing.Rows.Add(new Row { Id = 1, Name = "original" });
            await existing.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "duplicate" });
            return await scope.Env.SaveChangesAsync(scope, map ? MapUnique : null);
        });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        if (map)
        {
            Assert.True(exit.TryGetValue(out Err<TestError> err));
            Assert.IsType<Rejected>(err.Error.Value);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            var update = Assert.IsType<DbUpdateException>(die.Exception);
            Assert.IsType<SqliteException>(update.InnerException);
        }
        Assert.Equal(1, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_MapperThrows_DiesAndRollsBack()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("mapper");
        DatabaseFailure? observedFailure = null;
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = null! });
            return await scope.Env.SaveChangesAsync(scope, Throw);
        });
        bool Throw(DatabaseFailure observed, out TestError error)
        {
            observedFailure = observed;
            throw failure;
        }

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: DatabaseFailure { Cause: DbUpdateException } });
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed failed && ReferenceEquals(failed.Error, observedFailure));
        Assert.Equal(0, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_UnconfirmedCommitMapped_ReturnsTheApplicationError()
    {
        // Given
        await using var store = await Store.Open();
        store.Probe.BeforeCommit = _ => throw new IOException("unconfirmed");
        static bool Map(DatabaseFailure failure, out TestError error)
        {
            error = new Rejected(failure is CommitNotConfirmed ? "unconfirmed" : "unexpected");
            return true;
        }

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1), commitFailure: Map)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        Assert.Equal("unconfirmed", Assert.IsType<Rejected>(err.Error.Value).Reason);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_ReplyLostAfterPhysicalCommit_PreservesFailureAndCommittedRows()
    {
        // Given
        await using var store = await Store.Open();
        store.Probe.AfterCommit = () => throw new IOException("lost reply");

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1))
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: CommitNotConfirmed });
        Assert.Equal(1, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_BodyAndBothCleanupOperationsFail_RetainsAllFailures()
    {
        // Given
        await using var store = await Store.Open();
        var rollback = new InvalidOperationException("rollback");
        var dispose = new InvalidOperationException("dispose");
        store.Probe.RollbackFailure = rollback;
        store.Probe.DisposeFailure = dispose;
        var body = Scope<TestError, TestDb>.Gen<int>(scope => throw scope.Fail(new Rejected("body")));

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(rollback, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Die d && ReferenceEquals(d.Exception, dispose));
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed);
        Assert.Equal(1, store.Probe.Disposals);
    }

    private static bool MapUnique(DatabaseFailure failure, out TestError error)
    {
        if (failure is ConstraintViolation { Kind: ConstraintKind.Unique })
        {
            error = new Rejected("duplicate");
            return true;
        }
        error = default;
        return false;
    }

    [Fact]
    public async Task Transaction_EnvironmentFactoryThrows_RollsBackAndDisposes()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("environment");
        TestDb Environment(Store env, TestDb db) => throw failure;

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: Environment, body: Insert(1))
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(0, store.Probe.Saves);
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_RetryingExecutionStrategy_RejectsBeforeStartingTheTransaction()
    {
        // Given
        await using var store = await Store.Open(retry: true);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1))
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<NotSupportedException>(die.Exception);
        Assert.Equal(0, store.Probe.Saves);
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(0, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CommitMapperThrows_RetainsTheOriginalCommitFailure()
    {
        // Given
        await using var store = await Store.Open();
        var original = new IOException("commit");
        var mapperFailure = new InvalidOperationException("mapper");
        store.Probe.BeforeCommit = _ => throw original;
        bool Throw(DatabaseFailure failure, out TestError error) => throw mapperFailure;

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1), commitFailure: Throw)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(mapperFailure, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: CommitNotConfirmed failure }
            && ReferenceEquals(failure.Cause, original));
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_AfterCommitHookThrowsConstraintError_DoesNotClaimACommitRejection()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new SqliteException("post-commit hook", 19, 787);
        store.Probe.AfterCommit = () => throw failure;
        DatabaseFailure? observed = null;
        bool Observe(DatabaseFailure value, out TestError error)
        {
            observed = value;
            error = default;
            return false;
        }

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: Insert(1), commitFailure: Observe)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<CommitNotConfirmed>(observed);
        Assert.Same(failure, observed.Cause);
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(1, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_OuterScopeUsedInNestedGen_RejectsBeforeTheDatabaseCall()
    {
        // Given
        await using var store = await Store.Open();
        var body = Scope<TestError, TestDb>.Gen(async outer =>
            await outer.Run(Scope<TestError, TestDb>.Gen(async inner =>
            {
                inner.Env.Rows.Add(new Row { Id = 1, Name = "invalid scope" });
#pragma warning disable MEFF303 // The negative test checks the runtime guard independently of the analyzer.
                return await inner.Env.SaveChangesAsync(outer);
#pragma warning restore MEFF303
            })));

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal(0, store.Probe.Saves);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_NotAwaited_WaitsForThePendingOperationBeforeCommit()
    {
        // Given
        await using var store = await Store.Open();
        var allowSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Probe.BeforeSave = () => allowSave.Task;
        var body = Scope<TestError, TestDb>.Gen(scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "not awaited" });
            _ = scope.Env.SaveChangesAsync(scope);
            return new ValueTask<int>(1);
        });

        // When
        var running = EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken).AsTask();

        // Then
        try
        {
            Assert.False(running.IsCompleted);
            Assert.Equal(0, store.Probe.Commits);
            Assert.Equal(0, store.Probe.Disposals);
        }
        finally
        {
            allowSave.TrySetResult();
        }
        await running;
        Assert.Equal(1, store.Probe.Disposals);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_SecondSaveFails_RollsBackTheFirstSave()
    {
        // Given
        await using var store = await Store.Open();
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "first" });
            await scope.Env.SaveChangesAsync(scope);
            scope.Env.Rows.Add(new Row { Id = 2, Name = null! });
            return await scope.Env.SaveChangesAsync(scope);
        });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<DbUpdateException>(die.Exception);
        Assert.Equal(0, await store.Count());
    }

    private static bool NoMapping(DatabaseFailure failure, out TestError error)
    {
        error = default;
        return false;
    }

    private static bool MapForeignKey(DatabaseFailure failure, out TestError error)
    {
        if (failure is ConstraintViolation { Kind: ConstraintKind.ForeignKey })
        {
            error = new Rejected("foreign key");
            return true;
        }
        error = default;
        return false;
    }
}

public sealed record Rejected(string Reason);

[Error]
public partial union TestError(Rejected, DatabaseFailure);

public sealed class Row
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

public sealed class TestDb(DbContextOptions<TestDb> options, Probe probe) : DbContext(options)
{
    public Guid Instance { get; } = Guid.NewGuid();
    public DbSet<Row> Rows => Set<Row>();
    protected override void OnModelCreating(ModelBuilder builder) => builder.Entity<Row>().Property(row => row.Id).ValueGeneratedNever();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        probe.Saves++;
        if (probe.BeforeSave is { } before) await before();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        probe.Disposals++;
        if (probe.DisposeFailure is { } failure) throw failure;
    }
}

public sealed class Probe : DbTransactionInterceptor
{
    public int Saves { get; set; }
    public Func<Task>? BeforeSave { get; set; }
    public int Commits { get; set; }
    public int Rollbacks { get; set; }
    public int Disposals { get; set; }
    public bool RollbackCanBeCanceled { get; set; }
    public Action<CancellationToken>? BeforeCommit { get; set; }
    public Action? AfterCommit { get; set; }
    public Exception? RollbackFailure { get; set; }
    public Exception? DisposeFailure { get; set; }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Commits++;
        BeforeCommit?.Invoke(cancellationToken);
        return new(result);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        AfterCommit?.Invoke();
        return Task.CompletedTask;
    }

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Rollbacks++;
        RollbackCanBeCanceled = cancellationToken.CanBeCanceled;
        if (RollbackFailure is { } failure) throw failure;
        return new(result);
    }
}

public sealed class Store(SqliteConnection connection, DbContextOptions<TestDb> options, Probe probe) : IDbContextFactory<TestDb>, IAsyncDisposable
{
    public Probe Probe { get; } = probe;
    public TestDb CreateDbContext() => new(options, Probe);

    public static async Task<Store> Open(bool retry = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Rows (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE Children (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES Rows(Id) DEFERRABLE INITIALLY DEFERRED);
            """;
        await command.ExecuteNonQueryAsync();
        var probe = new Probe();
        var options = new DbContextOptionsBuilder<TestDb>().UseSqlite(connection, sqlite =>
        {
            if (retry) sqlite.ExecutionStrategy(dependencies => new TestRetryStrategy(dependencies));
        }).AddInterceptors(probe).Options;
        return new(connection, options, probe);
    }

    public async Task<long> Count(string table = "Rows")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = table == "Rows" ? "SELECT COUNT(*) FROM Rows" : "SELECT COUNT(*) FROM Children";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

public sealed class TestRetryStrategy(ExecutionStrategyDependencies dependencies) : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => true;
}
