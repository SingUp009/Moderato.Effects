---
title: 07 - Running Effects Concurrently
description: Run a shipping quote and stock check together with Effect.All, and see how the group waits for cleanup after cancellation.
---

# Running Effects Concurrently

In [chapter 4](/tutorial/04-composing-effects/), each step used the result of the previous one. At checkout, a shipping quote and a stock check can run at the same time. Each has the inputs it needs, and checkout needs both results. This chapter groups them with `Effect.All` and uses the resource cleanup from [chapter 6](/tutorial/06-managing-resources/) to see what happens when one step fails.

## Run the chapter

From the repository root, run the example.

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 07
```

The completed code is in [07_ConcurrentEffects.cs](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/07_ConcurrentEffects.cs).

## Build it yourself

Replace `Program.cs` in your tutorial console project with this program. It simulates the shipping and stock services so you can run it locally.

```csharp
using Moderato.Effects;
using Moderato.Results;

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
```

Run `dotnet run` to see both outcomes.

```text
Released shipping buffer.
Standard shipping: $5.00.
Units available: 12
Released shipping buffer after cancellation.
Checkout failed: Item is out of stock.
```

## Combine independent effects

```csharp
var checkout = Effect.All(shipping, stock, Concurrency.Parallel);
var result = await checkout.RunAsync();
```

`Effect.All` groups the two effects. Its default is to run them one at a time. With `Concurrency.Parallel`, both can make progress at the same time. Use this mode when the work can overlap safely. If both effects share a service, that service must support concurrent use.

On success, the result contains a tuple. Its first element is the shipping quote and its second is the stock count. The tuple follows the argument order even if the stock check finishes first.

## Wait for cleanup when a step fails

The second run simulates a shipping request that is still waiting when stock runs out. The stock check waits for the sample's `quoteStarted` signal so the shipping buffer is already open when the failure happens. That signal makes the demo repeatable. In checkout code, a stock check can report its result as soon as it has one.

Once the stock check reports its expected error, `Effect.All` requests cancellation of the shipping effect. The pending delay observes the scope's cancellation token and the shipping block ends. The scope releases the buffer, passing `Interrupted` to its release callback.

The group waits for that cleanup before it returns the stock error. In the output, “Released shipping buffer after cancellation.” therefore appears before “Checkout failed.” Effects that call an asynchronous API should pass along their cancellation token so the API can respond to that request.

## Try it

1. Swap the two arguments to `Effect.All` and update the tuple pattern and deconstruction. The output can stay the same because you now read the tuple in the new input order.
2. Change the stock count in the successful case. The checkout prints both values, with the shipping quote in its original position if you restore the argument order.

## Continue learning

You can now combine effects that depend on each other's results, or group independent effects that can safely run together. The [Concurrency guide](/concurrency/) covers limiting parallelism and collecting every outcome. See [Time](/time/) and [Retry and repeat](/retry-and-repeat/) for timeouts and for retrying operations that are safe to repeat.
