using Moderato.Results;

namespace Moderato.Effects.Tutorial;

internal static class ConcurrentEffectsChapter
{
    public static async Task RunAsync()
    {
        foreach (var inStock in new[] { true, false })
        {
            var quoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shipping = QuoteShipping(quoteStarted, waitForCancellation: !inStock);
            var stock = CheckStock(inStock, quoteStarted);

            var checkout = Effect.All(shipping, stock, Concurrency.Parallel);
            var result = await checkout.RunAsync();

            if (result is Ok<(string, int)>(var details))
            {
                var (quote, available) = details;
                Console.WriteLine(quote);
                Console.WriteLine($"Units available: {available}");
            }
            else if (result is Err<string>(var error))
            {
                Console.WriteLine($"Checkout failed: {error}");
            }
        }

        static Effect<string, string, EmptyEnvironment> QuoteShipping(
            TaskCompletionSource quoteStarted, bool waitForCancellation)
        {
            var responseBuffer = Resource.Make(
                acquire: Effect<EmptyEnvironment>.Sync(() => new StringWriter()),
                release: (writer, exit) => Effect<EmptyEnvironment>.Sync(() =>
                {
                    writer.Dispose();
                    Console.WriteLine(exit is Interrupted
                        ? "Released shipping buffer after cancellation."
                        : "Released shipping buffer.");
                    return Unit.Value;
                }));

            return Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
            {
                var writer = await scope.Acquire(responseBuffer);
                quoteStarted.SetResult();

                if (waitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken);

                await writer.WriteAsync("Standard shipping: $5.00.");
                return writer.ToString();
            });
        }

        static Effect<int, string, EmptyEnvironment> CheckStock(bool inStock, TaskCompletionSource quoteStarted) =>
            Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
            {
                if (inStock)
                    return 12;

                await quoteStarted.Task.WaitAsync(scope.CancellationToken);
                throw scope.Fail("Item is out of stock.");
            });
    }
}
