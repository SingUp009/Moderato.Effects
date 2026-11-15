using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class BeginFailureTests
{
    private sealed class Unreachable(string message) : DbException(message);

    private sealed class Contexts(Func<TestDb> create) : IDbContextFactory<TestDb>
    {
        public TestDb CreateDbContext() => create();
    }

    private static Effect<int, TestError, Contexts> InsertIn(Action onBody)
        => EfDatabase<Contexts>.Transaction(
            contexts: env => env,
            environment: (_, db) => db,
            body: Scope<TestError, TestDb>.Gen(async scope =>
            {
                onBody();
                scope.Env.Rows.Add(new Row { Id = 1, Name = "saved" });
                return await scope.Env.SaveChangesAsync(scope.CancellationToken);
            }));

    [Fact]
    public async Task Transaction_ContextCreationThrowsADatabaseException_FailsWithTheDatabaseFailure()
    {
        // Given
        var failure = new Unreachable("no route to the database");
        var bodyStarted = false;
        var contexts = new Contexts(() => throw failure);

        // When
        var exit = await InsertIn(() => bodyStarted = true).RunExitAsync(contexts, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> error));
        var database = Assert.IsType<DatabaseFailure>(error.Error.Value);
        Assert.IsType<UnclassifiedDatabaseFailure>(database.Value);
        Assert.Same(failure, database.Cause);
        Assert.False(bodyStarted);
    }

    [Fact]
    public async Task Transaction_BeginThrowsADatabaseException_FailsWithTheDatabaseFailureAndDisposesTheContext()
    {
        // Given
        var probe = new Probe();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "orders.db");
        var options = new DbContextOptionsBuilder<TestDb>().UseSqlite($"Data Source={missing}").AddInterceptors(probe).Options;
        var bodyStarted = false;
        var contexts = new Contexts(() => new TestDb(options, probe));

        // When
        var exit = await InsertIn(() => bodyStarted = true).RunExitAsync(contexts, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> error));
        var database = Assert.IsType<DatabaseFailure>(error.Error.Value);
        Assert.IsType<UnclassifiedDatabaseFailure>(database.Value);
        Assert.IsType<SqliteException>(database.Cause);
        Assert.False(bodyStarted);
        Assert.Equal(0, probe.Rollbacks);
        Assert.Equal(1, probe.Disposals);
    }

    [Fact]
    public async Task Transaction_ContextCreationThrowsAnotherException_Dies()
    {
        // Given
        var failure = new InvalidOperationException("misconfigured factory");
        var contexts = new Contexts(() => throw failure);

        // When
        var exit = await InsertIn(static () => { }).RunExitAsync(contexts, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
    }

    private sealed class CancelsThenThrows(CancellationTokenSource caller) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionStartingEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            caller.Cancel();
            throw new Unreachable("Operation cancelled by user.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_ProviderReportsCancellationAsADatabaseException_IsInterrupted(bool atContextCreation)
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new Probe();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<TestDb>().UseSqlite(connection).AddInterceptors(probe, new CancelsThenThrows(caller)).Options;
        var contexts = new Contexts(() =>
        {
            if (!atContextCreation) return new TestDb(options, probe);
            caller.Cancel();
            throw new Unreachable("Operation cancelled by user.");
        });
        List<ExitCase> reported = [];
        var runOptions = new EffectRunOptions { UnobservedFailureHandler = failure => { lock (reported) reported.Add(failure); } };
        var bodyStarted = false;

        // When
        var exit = await InsertIn(() => bodyStarted = true).RunExitAsync(contexts, caller.Token, runOptions);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.False(bodyStarted);
        Assert.Empty(reported);
        Assert.Equal(0, probe.Rollbacks);
        Assert.Equal(atContextCreation ? 0 : 1, probe.Disposals);
    }

    private static Effect<int, TestError, Contexts> InsertWith(TryMapDatabaseFailure<TestError> beginFailure)
        => EfDatabase<Contexts>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: TestEffects.Insert(1), beginFailure: beginFailure);

    private static Contexts CannotOpen(Probe probe)
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "orders.db");
        var options = new DbContextOptionsBuilder<TestDb>().UseSqlite($"Data Source={missing}").AddInterceptors(probe).Options;
        return new Contexts(() => new TestDb(options, probe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_BeginFailureMapped_ReturnsTheApplicationError(bool atContextCreation)
    {
        // Given
        var cause = new Unreachable("no route to the database");
        var contexts = atContextCreation ? new Contexts(() => throw cause) : CannotOpen(new Probe());
        DatabaseFailure observed = default;
        bool Map(DatabaseFailure failure, out TestError error)
        {
            observed = failure;
            error = new Rejected("database unavailable");
            return true;
        }

        // When
        var exit = await InsertWith(Map).RunExitAsync(contexts, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> error));
        Assert.Equal(new Rejected("database unavailable"), error.Error.Value);
        Assert.IsType<UnclassifiedDatabaseFailure>(observed.Value);
        if (atContextCreation) Assert.Same(cause, observed.Cause);
        else Assert.IsType<SqliteException>(observed.Cause);
    }

    [Fact]
    public async Task Transaction_BeginFailureMapperDeclines_ReturnsTheDatabaseFailure()
    {
        // Given
        var probe = new Probe();
        static bool Decline(DatabaseFailure failure, out TestError error)
        {
            error = default;
            return false;
        }

        // When
        var exit = await InsertWith(Decline).RunExitAsync(CannotOpen(probe), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> error));
        Assert.IsType<UnclassifiedDatabaseFailure>(Assert.IsType<DatabaseFailure>(error.Error.Value).Value);
        Assert.Equal(1, probe.Disposals);
    }

    [Fact]
    public async Task Transaction_BeginFailureMapperThrows_DiesAndRetainsTheOriginalFailure()
    {
        // Given
        var probe = new Probe();
        var mapping = new InvalidOperationException("mapper");
        bool Throw(DatabaseFailure failure, out TestError error) => throw mapping;

        // When
        var exit = await InsertWith(Throw).RunExitAsync(CannotOpen(probe), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(mapping, die.Exception);
        var retained = Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value);
        Assert.IsType<UnclassifiedDatabaseFailure>(retained.Error);
        Assert.Equal(1, probe.Disposals);
    }

    [Fact]
    public async Task Transaction_BeginSucceeds_DoesNotCallTheBeginFailureMapper()
    {
        // Given
        await using var store = await Store.Open();
        var called = false;
        bool Map(DatabaseFailure failure, out TestError error)
        {
            called = true;
            error = default;
            return false;
        }

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: TestEffects.Insert(1), beginFailure: Map)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Ok<int> _));
        Assert.False(called);
        Assert.Equal(1, await store.Count());
    }
}
