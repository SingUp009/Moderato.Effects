using ConsoleAppFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moderato.Effects;
using Moderato.Effects.EntityFrameworkCore;
using Moderato.Results;

var app = ConsoleApp.Create();
app.Add("create", Commands.Create);
app.Add("read", Commands.Read);
app.Add("update", Commands.Update);
app.Add("delete", Commands.Delete);
await app.RunAsync(args);

internal static class Commands
{
    public static Task Create(int id, string name, string database = "orders.db", CancellationToken cancellationToken = default)
        => Execute(Orders.Create(id, name), database, cancellationToken);

    public static Task Read(int id, string database = "orders.db", CancellationToken cancellationToken = default)
        => Execute(Orders.Read(id), database, cancellationToken);

    public static Task Update(int id, string name, string database = "orders.db", CancellationToken cancellationToken = default)
        => Execute(Orders.Update(id, name), database, cancellationToken);

    public static Task Delete(int id, string database = "orders.db", CancellationToken cancellationToken = default)
        => Execute(Orders.Delete(id), database, cancellationToken);

    private static async Task Execute(Effect<OrderView, OrderError, AppEnv> effect, string database, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = database };
        var options = new DbContextOptionsBuilder<OrdersDb>().UseSqlite(connection.ToString()).Options;
        var env = new AppEnv(new OrdersDbFactory(options));
        await using (var db = await env.Contexts.CreateDbContextAsync(cancellationToken))
            await db.Database.EnsureCreatedAsync(cancellationToken);

        var exit = await effect.RunExitAsync(env, cancellationToken);
        switch (exit)
        {
            case Ok<OrderView>(var order):
                Console.WriteLine($"OK {order.Id}: {order.Name}");
                break;
            case Err<OrderError>(var error):
                Console.Error.WriteLine(error switch
                {
                    OrderNotFound e => $"NOT_FOUND {e.Id}",
                    OrderAlreadyExists e => $"ALREADY_EXISTS {e.Id}",
                    InvalidOrder e => $"INVALID {e.Reason}",
                    DatabaseFailure failure => $"DATABASE {failure.GetType().Name}: {failure.Cause.Message}",
                });
                Environment.ExitCode = 2;
                break;
            case Die die:
                Console.Error.WriteLine(die.Exception);
                Environment.ExitCode = 1;
                break;
            case Interrupted:
                Console.Error.WriteLine("INTERRUPTED");
                Environment.ExitCode = 130;
                break;
        }
    }
}
