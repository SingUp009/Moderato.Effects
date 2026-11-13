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
}
