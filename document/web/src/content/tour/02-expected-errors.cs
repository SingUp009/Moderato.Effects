using Moderato.Effects;
using Moderato.Results;

foreach (var quantity in new[] { 2, 0 })
{
    Result<string, OrderError> result = await PrepareOrder(quantity).RunAsync();

    if (result is Ok<string>(var message))
        Console.WriteLine(message);
    else if (result is Err<OrderError>(InvalidQuantity invalid))
        Console.WriteLine($"Invalid quantity: {invalid.Quantity}");
}

static Effect<string, OrderError, EmptyEnvironment> PrepareOrder(int quantity) =>
    Effect.Gen(async (Scope<OrderError, EmptyEnvironment> scope) =>
    {
        var accepted = scope.Get(ValidateQuantity(quantity));
        await Console.Out.WriteLineAsync("Preparing the order.");
        return $"Order ready (quantity: {accepted}).";
    });

static Result<int, OrderError> ValidateQuantity(int quantity) =>
    quantity > 0 ? new Ok<int>(quantity) : new Err<OrderError>(new InvalidQuantity(quantity));

public sealed record InvalidQuantity(int Quantity);
public sealed record OutOfStock(string Item);

[Error]
public partial union OrderError(InvalidQuantity, OutOfStock);
