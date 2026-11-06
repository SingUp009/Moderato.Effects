using Moderato.Effects;
using Moderato.Effects.EntityFrameworkCore;

[Error]
public partial union InvalidError(OrderNotFound);

public static class MissingDatabaseError
{
    public static object Build() => EfDatabase<AppEnv>.Transaction(
        contexts: env => env.Contexts,
        environment: (_, db) => db,
        body: Scope<InvalidError, OrdersDb>.Gen(_ => new ValueTask<int>(1)));
}
