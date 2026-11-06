using Microsoft.EntityFrameworkCore;
using Moderato.Effects;
using Moderato.Effects.EntityFrameworkCore;
using Moderato.Results;

public sealed record OrderView(int Id, string Name);
public sealed record OrderNotFound(int Id);
public sealed record OrderAlreadyExists(int Id);
public sealed record InvalidOrder(string Reason);

[Error]
public partial union OrderError(OrderNotFound, OrderAlreadyExists, InvalidOrder, DatabaseFailure);

public sealed record AppEnv(IDbContextFactory<OrdersDb> Contexts);
public sealed record OrderDb(OrdersDb Context);

public static class Orders
{
    public static Effect<OrderView, OrderError, AppEnv> Create(int id, string name)
        => WithDatabase(async (db, scope) =>
        {
            Validate(id, name, scope);
            var order = new Order { Id = id, Name = name };
            db.Orders.Add(order);
            await db.SaveChangesAsync(scope, MapCreateFailure);
            return new OrderView(order.Id, order.Name);

            bool MapCreateFailure(DatabaseFailure failure, out OrderError error)
            {
                // Orders has no unique constraint other than its primary key.
                if (failure is ConstraintViolation { Kind: ConstraintKind.Unique })
                {
                    error = new OrderAlreadyExists(id);
                    return true;
                }
                error = default;
                return false;
            }
        });

    public static Effect<OrderView, OrderError, AppEnv> Read(int id)
        => WithDatabase(async (db, scope) =>
        {
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(order => order.Id == id, scope.CancellationToken)
                ?? throw scope.Fail(new OrderNotFound(id));
            return new OrderView(order.Id, order.Name);
        });

    public static Effect<OrderView, OrderError, AppEnv> Update(int id, string name)
        => WithDatabase(async (db, scope) =>
        {
            Validate(id, name, scope);
            var order = await db.Orders.SingleOrDefaultAsync(order => order.Id == id, scope.CancellationToken)
                ?? throw scope.Fail(new OrderNotFound(id));
            order.Name = name;
            await db.SaveChangesAsync(scope);
            return new OrderView(order.Id, order.Name);
        });

    public static Effect<OrderView, OrderError, AppEnv> Delete(int id)
        => WithDatabase(async (db, scope) =>
        {
            var order = await db.Orders.SingleOrDefaultAsync(order => order.Id == id, scope.CancellationToken)
                ?? throw scope.Fail(new OrderNotFound(id));
            db.Orders.Remove(order);
            await db.SaveChangesAsync(scope);
            return new OrderView(order.Id, order.Name);
        });

    private static Effect<OrderView, OrderError, AppEnv> WithDatabase(
        Func<OrdersDb, Scope<OrderError, OrderDb>, ValueTask<OrderView>> body)
        => EfDatabase<AppEnv>.Transaction(
            contexts: env => env.Contexts,
            environment: (_, db) => new OrderDb(db),
            body: Scope<OrderError, OrderDb>.Gen(scope => body(scope.Env.Context, scope)));

    private static void Validate(int id, string name, Scope<OrderError, OrderDb> scope)
    {
        if (id <= 0) throw scope.Fail(new InvalidOrder("id must be positive"));
        if (string.IsNullOrWhiteSpace(name)) throw scope.Fail(new InvalidOrder("name must not be blank"));
    }
}
