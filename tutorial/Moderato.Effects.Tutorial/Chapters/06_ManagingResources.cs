using Moderato.Results;

namespace Moderato.Effects.Tutorial;

internal static class ManagingResourcesChapter
{
    public static async Task RunAsync()
    {
        var draftWriter = Resource.Make(
            acquire: Effect<EmptyEnvironment>.Sync(() =>
            {
                Console.WriteLine("Opening order draft.");
                return new StringWriter();
            }),
            release: (writer, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                writer.Dispose();
                Console.WriteLine("Closed order draft.");
                return Unit.Value;
            }));

        foreach (var quantity in new[] { 2, 0 })
        {
            var prepareOrder = Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
            {
                var writer = await scope.Acquire(draftWriter);

                if (quantity <= 0)
                    throw scope.Fail("Quantity must be greater than zero.");

                await writer.WriteAsync($"Order draft (quantity: {quantity}).");
                return writer.ToString();
            });

            Result<string, string> result = await prepareOrder.RunAsync();
            if (result is Ok<string>(var draft))
                Console.WriteLine(draft);
            else if (result is Err<string>(var error))
                Console.WriteLine($"Order rejected: {error}");
        }
    }
}
