using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class NestedTransactionTests
{
    private sealed class SeparateConnections(string connectionString, Probe probe) : IDbContextFactory<TestDb>
    {
        public TestDb CreateDbContext()
            => new(new DbContextOptionsBuilder<TestDb>().UseSqlite(connectionString).AddInterceptors(probe).Options, probe);
    }

    private static Effect<int, TestError, (TFactory Factory, TestDb Db)> RunsAnotherTransaction<TFactory>()
        where TFactory : IDbContextFactory<TestDb>
        => Scope<TestError, (TFactory Factory, TestDb Db)>.Gen(async scope =>
        {
            var inner = EfDatabase<TFactory>.Transaction(env => env, (_, db) => db, TestEffects.Insert(2));
            return await scope.Run(inner.Provide<(TFactory Factory, TestDb Db)>(static env => env.Factory));
        });

    [Fact]
    public async Task Transaction_NestedOnSeparateConnections_WaitsForTheOuterLockThenFails()
    {
        // Given
        var connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=1";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = keepAlive.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Rows (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var probe = new Probe();
        var factory = new SeparateConnections(connectionString, probe);

        // When
        var exit = await EfDatabase<SeparateConnections>.Transaction(
            contexts: env => env, environment: (env, db) => (env, db), body: RunsAnotherTransaction<SeparateConnections>())
            .RunExitAsync(factory, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> error));
        var database = Assert.IsType<DatabaseFailure>(error.Error.Value);
        Assert.IsType<SqliteException>(database.Cause);
        await using var count = keepAlive.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Rows";
        Assert.Equal(0L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, probe.Commits);
        Assert.Equal(1, probe.Rollbacks);
        Assert.Equal(2, probe.Disposals);
    }

    [Fact]
    public async Task Transaction_NestedOnASharedConnection_DiesAndRollsBackTheOuterTransaction()
    {
        // Given
        await using var store = await Store.Open();

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (env, db) => (env, db), body: RunsAnotherTransaction<Store>())
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal(0, await store.Count());
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(2, store.Probe.Disposals);
    }
}
