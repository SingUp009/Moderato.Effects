using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moderato.Effects;
using Moderato.Effects.EntityFrameworkCore;
using Moderato.Results;

// Given
await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var options = new DbContextOptionsBuilder<OrdersDb>().UseSqlite(connection).Options;
var env = new AppEnv(new OrdersDbFactory(options));
await using (var db = new OrdersDb(options)) await db.Database.EnsureCreatedAsync();

var cause = new IOException("provider");
DatabaseFailure failure = new UnclassifiedDatabaseFailure(cause);

await using (var seed = new OrdersDb(options))
{
    seed.Orders.Add(new Order { Id = 2, Name = "original" });
    await seed.SaveChangesAsync();
}
await using var stale = new OrdersDb(options);
var staleOrder = await stale.Orders.SingleAsync(order => order.Id == 2);
await using (var winner = new OrdersDb(options))
{
    winner.Orders.Remove(await winner.Orders.SingleAsync(order => order.Id == 2));
    await winner.SaveChangesAsync();
}
staleOrder.Name = "pending";
var conflict = Scope<OrderError, OrdersDb>.Gen(async scope =>
    await scope.Env.SaveChangesAsync(scope, static (DatabaseFailure databaseFailure, out OrderError error) =>
    {
        error = databaseFailure;
        return true;
    }));

// When
OrderError nested = failure;
var created = await Orders.Create(1, "Coffee").RunExitAsync(env, CancellationToken.None);
var read = await Orders.Read(1).RunExitAsync(env, CancellationToken.None);
var updated = await Orders.Update(1, "Tea").RunExitAsync(env, CancellationToken.None);
var deleted = await Orders.Delete(1).RunExitAsync(env, CancellationToken.None);
var missing = await Orders.Read(1).RunExitAsync(env, CancellationToken.None);
var conflicted = await conflict.RunExitAsync(stale, CancellationToken.None);

// Then
if (nested.Value is not DatabaseFailure { Value: UnclassifiedDatabaseFailure unclassified }
    || !ReferenceEquals(cause, unclassified.Cause)
    || conflicted.Value is not Err<OrderError> { Error.Value: DatabaseFailure { Value: OptimisticConcurrencyConflict concurrency } }
    || concurrency.Cause is not DbUpdateConcurrencyException
    || created.Value is not Ok<OrderView> { Value.Name: "Coffee" }
    || read.Value is not Ok<OrderView> { Value.Name: "Coffee" }
    || updated.Value is not Ok<OrderView> { Value.Name: "Tea" }
    || deleted.Value is not Ok<OrderView> { Value.Name: "Tea" }
    || missing.Value is not Err<OrderError> { Error.Value: OrderNotFound { Id: 1 } })
{
    throw new InvalidOperationException("EF-only package consumer lost CRUD or generated error union behavior.");
}
Console.WriteLine("PASS: EF-only package consumer, generated unions, SQLite CRUD and optimistic concurrency");
