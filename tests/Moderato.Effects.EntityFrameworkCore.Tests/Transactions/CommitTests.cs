using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static SQLitePCL.raw;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class CommitTests
{

    [Fact]
    public async Task Transaction_ReaderBlocksCommit_ClassifiesRejectionAndRollsBack()
    {
        // Given
        var directory = Directory.CreateTempSubdirectory("moderato-commit-");
        try
        {
            var token = TestContext.Current.CancellationToken;
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory.FullName, "test.db"),
                DefaultTimeout = 1,
                Pooling = false,
            }.ConnectionString;
            await using var reader = new SqliteConnection(connectionString);
            await reader.OpenAsync(token);
            await using var command = reader.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE Rows (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync(token);

            var writer = new SqliteConnection(connectionString);
            var probe = new Probe();
            var options = new DbContextOptionsBuilder<TestDb>().UseSqlite(writer).AddInterceptors(probe).Options;
            await using var store = new Store(writer, options, probe);
            await writer.OpenAsync(token);
            await using var readTransaction = reader.BeginTransaction(deferred: true);
            command.Transaction = readTransaction;
            command.CommandText = "SELECT COUNT(*) FROM Rows";
            await command.ExecuteScalarAsync(token);

            static bool Map(DatabaseFailure failure, out TestError error)
            {
                error = failure;
                return true;
            }

            // When
            var transaction = EfDatabase<Store>.Transaction(
                contexts: env => env, environment: (_, db) => db, body: TestEffects.Insert(1), commitFailure: Map);
            var blocked = await transaction.RunExitAsync(store, token);
            await readTransaction.RollbackAsync(token);
            var rowsAfterRollback = await store.Count();
            var recovered = await transaction.RunExitAsync(store, token);

            // Then
            var mapped = Assert.IsType<DatabaseFailure>(Assert.IsType<Err<TestError>>(blocked.Value).Error.Value);
            var failure = Assert.IsType<UnclassifiedDatabaseFailure>(mapped.Value);
            Assert.Equal(SQLITE_BUSY, Assert.IsType<SqliteException>(failure.Cause).SqliteErrorCode);
            Assert.Equal(0, rowsAfterRollback);
            Assert.Equal(1, Assert.IsType<Ok<int>>(recovered.Value).Value);
            Assert.Equal(1, await store.Count());
            Assert.Equal(2, probe.Commits);
            Assert.Equal(1, probe.Rollbacks);
            Assert.Equal(2, probe.Disposals);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: mapper)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        var unknown = Assert.IsType<CommitNotConfirmed>(Assert.IsType<DatabaseFailure>(err.Error.Value).Value);
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

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        Assert.IsType<CommitNotConfirmed>(Assert.IsType<DatabaseFailure>(err.Error.Value).Value);
        Assert.Equal(0, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CanceledDuringCommitInsideAGroup_ReportsTheUnconfirmedCommit()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        store.Probe.BeforeCommit = token =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        };
        List<ExitCase> reported = [];
        var options = new EffectRunOptions { UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); } };
        var transaction = EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: TestEffects.Insert(1));

        // When
        var exit = await Effect.All([transaction]).RunExitAsync(store, cancellation.Token, options);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        var failed = Assert.IsType<Failed>(Assert.Single(reported).Value);
        Assert.IsType<CommitNotConfirmed>(failed.Error);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_CanceledBeforeCommit_DoesNotCallCommit()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        var body = TestEffects.Insert(1).Map(value => { cancellation.Cancel(); return value; });

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

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
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
    public async Task Transaction_UnconfirmedCommitMapped_ReturnsTheApplicationError()
    {
        // Given
        await using var store = await Store.Open();
        store.Probe.BeforeCommit = _ => throw new IOException("unconfirmed");
        static bool Map(DatabaseFailure failure, out TestError error)
        {
            error = new Rejected(failure.Value is CommitNotConfirmed ? "unconfirmed" : "unexpected");
            return true;
        }

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: Map)
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

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: CommitNotConfirmed });
        Assert.Equal(1, await store.Count());
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

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: Throw)
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
        var failure = new SqliteException("post-commit hook", SQLITE_CONSTRAINT, SQLITE_CONSTRAINT_FOREIGNKEY);
        store.Probe.AfterCommit = () => throw failure;
        DatabaseFailure observed = default;
        bool Observe(DatabaseFailure value, out TestError error)
        {
            observed = value;
            error = default;
            return false;
        }

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: Observe)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<CommitNotConfirmed>(observed.Value);
        Assert.Same(failure, observed.Cause);
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(1, await store.Count());
    }

    private static bool NoMapping(DatabaseFailure failure, out TestError error)
    {
        error = default;
        return false;
    }

    private static bool MapForeignKey(DatabaseFailure failure, out TestError error)
    {
        if (failure.Value is ConstraintViolation { Kind: ConstraintKind.ForeignKey })
        {
            error = new Rejected("foreign key");
            return true;
        }
        error = default;
        return false;
    }
}
