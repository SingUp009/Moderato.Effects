using Moderato.Results;

namespace Moderato.Effects.Tutorial;

internal static class ComposingEffectsChapter
{
    public static async Task RunAsync()
    {
        foreach (var quantity in new[] { 2, 0 })
        {
            Result<string, string> result = await PlaceOrder(quantity).RunAsync();

            if (result is Ok<string>(var confirmation))
                Console.WriteLine(confirmation);
            else if (result is Err<string>(var error))
                Console.WriteLine($"Order rejected: {error}");
        }
    }

    private static Effect<string, string, EmptyEnvironment> PlaceOrder(int quantity) =>
        Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
        {
            var prepared = await scope.Run(PrepareOrder(quantity));
            return await scope.Run(ConfirmOrder(prepared));
        });

    private static Effect<int, string, EmptyEnvironment> PrepareOrder(int quantity) =>
        Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
        {
            var accepted = scope.Get(ValidateQuantity(quantity));
            await Console.Out.WriteLineAsync("Preparing the order.");
            return accepted;
        });

    private static Effect<string, string, EmptyEnvironment> ConfirmOrder(int quantity) =>
        Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
        {
            await Console.Out.WriteLineAsync("Confirming the order.");
            return $"Order confirmed (quantity: {quantity}).";
        });

    private static Result<int, string> ValidateQuantity(int quantity)
    {
        if (quantity <= 0)
            return new Err<string>("Quantity must be greater than zero.");

        return new Ok<int>(quantity);
    }
}
