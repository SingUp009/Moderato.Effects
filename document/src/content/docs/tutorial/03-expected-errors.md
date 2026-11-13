---
title: 03 - Handling Expected Errors
---

# Handling Expected Errors

In [chapter 1](../01-results/), validating an order quantity returned a `Result`. In [chapter 2](../02-first-effect/), an effect prepared an order. Put them together so the order is prepared only when validation succeeds.

## Run the chapter

From the repository root, run:

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 03
```

The completed code is in [03_ExpectedErrors.cs](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/03_ExpectedErrors.cs).

## Build it yourself

In the console project from chapter 2, replace `Program.cs` with:

```csharp
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
```

Run `dotnet run`. The output is:

```text
Preparing the order.
Order ready (quantity: 2).
Invalid quantity: Quantity must be greater than zero.
```

The first order reaches preparation. The second produces only an error message.

## Read a result inside Gen

```csharp
var accepted = scope.Get(ValidateQuantity(quantity));
```

`ValidateQuantity` runs inside the Gen block, when the effect runs. `scope.Get` reads the `Result<int, string>` it returns:

- For an `Ok<int>`, `scope.Get` returns the accepted quantity. The block continues.
- For an `Err<string>`, `scope.Get` ends the block with that error. The preparation message and the final `return` are skipped.

The validator still works as an ordinary method that returns a `Result`. The Gen block decides how the result affects the rest of the work.

## Read the error type

In chapter 2, the scope used `Never` because the effect had no expected errors. Here it uses `Scope<string, EmptyEnvironment>` so it can propagate the validator's string error.

The resulting `Effect<string, string, EmptyEnvironment>` succeeds with an order message, can fail with a validation message and needs no environment to be supplied. Awaiting `RunAsync` gives you a `Result<string, string>`. Both values are strings, but their cases tell you which outcome you received. The caller uses pattern matching to extract the message from either case.

## Try it

1. Replace `2` with `3`. The successful message should show quantity `3`.
2. Replace `0` with `-1`. Predict whether the block prints a second preparation message.
3. Change the validator's error message. Run the program and check that the new message reaches the caller.

## Next

In the [next chapter](../04-composing-effects/), run effects inside a Gen block and pass a successful value from one step to the next.
