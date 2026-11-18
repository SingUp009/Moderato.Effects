using Moderato.Results;

namespace Moderato.Effects.Tutorial;

internal static class FirstEffectChapter
{
    public static async Task RunAsync()
    {
        var prepareOrder = Effect.Gen(async (Scope<Never, EmptyEnvironment> scope) =>
        {
            await Console.Out.WriteLineAsync("Preparing the order.");
            return "Order ready.";
        });

        Console.WriteLine("Effect created.");

        Result<string, Never> result = await prepareOrder.RunAsync();

        if (result is Ok<string>(var message))
            Console.WriteLine(message);
    }
}
