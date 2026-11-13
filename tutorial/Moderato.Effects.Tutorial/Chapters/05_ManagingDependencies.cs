using Moderato.Results;

namespace Moderato.Effects.Tutorial;

internal static class ManagingDependenciesChapter
{
    public static async Task RunAsync()
    {
        var prepareOrder = Effect.Gen(async (Scope<Never, TextWriter> scope) =>
        {
            await scope.Env.WriteLineAsync("Preparing the order.");
            return "Order ready.";
        });

        Result<string, Never> result = await prepareOrder.RunAsync(Console.Out);
        if (result is Ok<string>(var message))
            Console.WriteLine(message);

        using var buffer = new StringWriter();
        await prepareOrder.RunAsync(buffer);
        Console.Write($"Captured: {buffer}");
    }
}
