using Moderato.Effects;
using Moderato.Results;

foreach (var quantity in new[] { 2, 0 })
{
    Result<string, string> result = await PrepareOrder(quantity).RunAsync();

    if (result is Ok<string>(var message))
        Console.WriteLine(message);
    else if (result is Err<string>(var error))
        Console.WriteLine($"Invalid quantity: {error}");
}

static Effect<string, string, EmptyEnvironment> PrepareOrder(int quantity) =>
    Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
    {
        var accepted = scope.Get(ValidateQuantity(quantity));
        await Console.Out.WriteLineAsync("Preparing the order.");
        return $"Order ready (quantity: {accepted}).";
    });

static Result<int, string> ValidateQuantity(int quantity)
{
    if (quantity <= 0)
        return new Err<string>("Quantity must be greater than zero.");

    return new Ok<int>(quantity);
}
