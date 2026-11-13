using Moderato.Results;

namespace Moderato.Effects.Tutorial;

internal static class ExpectedErrorsChapter
{
    public static async Task RunAsync()
    {
        foreach (var quantity in new[] { 2, 0 })
        {
            Result<string, string> result = await PrepareOrder(quantity).RunAsync();

            if (result is Ok<string>(var message))
                Console.WriteLine(message);
            else if (result is Err<string>(var error))
                Console.WriteLine($"Invalid quantity: {error}");
        }
    }

    private static Effect<string, string, EmptyEnvironment> PrepareOrder(int quantity) =>
        Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
        {
            var accepted = scope.Get(ValidateQuantity(quantity));
            await Console.Out.WriteLineAsync("Preparing the order.");
            return $"Order ready (quantity: {accepted}).";
        });

    private static Result<int, string> ValidateQuantity(int quantity)
    {
        if (quantity <= 0)
            return new Err<string>("Quantity must be greater than zero.");

        return new Ok<int>(quantity);
    }
}
