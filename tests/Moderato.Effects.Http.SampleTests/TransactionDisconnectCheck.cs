using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moderato.Effects.EntityFrameworkCore;

namespace Moderato.Effects.Http.Samples;

[Error]
internal partial union DisconnectError(DatabaseFailure);

internal static class TransactionDisconnectCheck
{
    private const string PATH = "/http-sample/disconnect-transaction";

    internal static async Task Map(WebApplication app, SqliteConnection database)
    {
        await using var command = database.CreateCommand();
        command.CommandText = "CREATE TABLE Writes (Id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<WriteDb>().UseSqlite(database.ConnectionString).Options;
        var factory = new WriteDbFactory(options);
        var observed = app.Services.GetRequiredService<ObservedFailures>();
        var body = Scope<DisconnectError, WriteDb>.Gen(async scope =>
        {
            await scope.Env.Database.ExecuteSqlRawAsync("INSERT INTO Writes VALUES (1);", scope.CancellationToken);
            observed.TransactionWritten.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken);
            return 1;
        });
        var transaction = EfDatabase<WriteDbFactory>.Transaction(env => env, (_, db) => db, body);
        app.MapPost(PATH, async (HttpContext context, EffectRequest request) =>
        {
            try
            {
                await request.RunAsync(transaction, factory);
                observed.TransactionEnded.SetResult(false);
            }
            catch (OperationCanceledException exception)
            {
                observed.TransactionEnded.SetResult(exception.CancellationToken == context.RequestAborted);
            }
            catch (Exception exception)
            {
                observed.TransactionEnded.SetException(exception);
                throw;
            }
        }).ExcludeFromDescription();
    }

    internal static async Task Run(HttpClient client, ObservedFailures observed, string connectionString)
    {
        // Given
        using var disconnect = new CancellationTokenSource();

        // When
        var request = client.PostAsync(PATH, null, disconnect.Token);
        try
        {
            await observed.TransactionWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await disconnect.CancelAsync();
        }
        try { using var response = await request; }
        catch (OperationCanceledException) when (disconnect.IsCancellationRequested) { }
        var interrupted = await observed.TransactionEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Then
        await using var reader = new SqliteConnection(connectionString);
        await reader.OpenAsync();
        await using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Writes;";
        if (!interrupted || await command.ExecuteScalarAsync() is not 0L)
        {
            throw new InvalidOperationException("A disconnect before commit must roll back the write.");
        }
        Console.WriteLine("PASS: real client disconnection rolls back the transaction");
    }

    private sealed class WriteDb(DbContextOptions<WriteDb> options) : DbContext(options);

    private sealed class WriteDbFactory(DbContextOptions<WriteDb> options) : IDbContextFactory<WriteDb>
    {
        public WriteDb CreateDbContext() => new(options);
    }
}
