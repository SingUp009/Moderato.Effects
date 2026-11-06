using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

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
