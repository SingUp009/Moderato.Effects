using Moderato.Effects;
using Moderato.Results;

public sealed record Order(int Id, string Sku, int Quantity);
public sealed record CreateOrderRequest(string Sku, int Quantity);
public sealed record ValidationError(string Message);
public sealed record DuplicateOrder(string Sku);

[Error]
public partial union CreateOrderError(ValidationError, DuplicateOrder);

[Environment]
public sealed partial record AppEnv(OrderStore Orders);

public sealed class OrderStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Order> orders = new(StringComparer.Ordinal);
    private int nextId;

    public Result<Order, CreateOrderError> Create(CreateOrderRequest command)
    {
        if (string.IsNullOrWhiteSpace(command.Sku) || command.Quantity <= 0)
            return new Err<CreateOrderError>(new ValidationError("SKU and a positive quantity are required."));
        var order = new Order(Interlocked.Increment(ref nextId), command.Sku, command.Quantity);
        return orders.TryAdd(command.Sku, order)
            ? new Ok<Order>(order)
            : new Err<CreateOrderError>(new DuplicateOrder(command.Sku));
    }
}

public static class Orders<R> where R : IHas<OrderStore>
{
    public static Effect<Order, CreateOrderError, R> Create(CreateOrderRequest command)
    {
        Effect<Result<Order, CreateOrderError>, CreateOrderError, R> read =
            Effect<R>.Access(env => env.Resolve<OrderStore>().Create(command));
        return read.AndThen(result => Effect<R>.FromResult(result));
    }
}
