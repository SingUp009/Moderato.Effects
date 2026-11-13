---
title: 04 - Composing Effects
description: Run effects in sequence with scope.Run and pass a prepared order to the confirmation step.
---

# Composing Effects

In [chapter 3](/tutorial/03-expected-errors/), `scope.Get` let a validation result decide whether order preparation could continue. Add a second step now. Prepare the order, then pass its accepted quantity to an effect that confirms it.

## Run the chapter

From the repository root, run:

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 04
```

The completed code is in [04_ComposingEffects.cs](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/04_ComposingEffects.cs).

## Build it yourself

Replace `Program.cs` in your tutorial console project with:

```csharp
using Moderato.Effects;
using Moderato.Results;

foreach (var quantity in new[] { 2, 0 })
{
    Result<string, string> result = await PlaceOrder(quantity).RunAsync();

    if (result is Ok<string>(var confirmation))
        Console.WriteLine(confirmation);
    else if (result is Err<string>(var error))
        Console.WriteLine($"Order rejected: {error}");
}

static Effect<string, string, EmptyEnvironment> PlaceOrder(int quantity) =>
    Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
    {
        var prepared = await scope.Run(PrepareOrder(quantity));
        return await scope.Run(ConfirmOrder(prepared));
    });

static Effect<int, string, EmptyEnvironment> PrepareOrder(int quantity) =>
    Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
    {
        var accepted = scope.Get(ValidateQuantity(quantity));
        await Console.Out.WriteLineAsync("Preparing the order.");
        return accepted;
    });

static Effect<string, string, EmptyEnvironment> ConfirmOrder(int quantity) =>
    Effect.Gen(async (Scope<string, EmptyEnvironment> scope) =>
    {
        await Console.Out.WriteLineAsync("Confirming the order.");
        return $"Order confirmed (quantity: {quantity}).";
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
Confirming the order.
Order confirmed (quantity: 2).
Order rejected: Quantity must be greater than zero.
```

## Pass a value to the next effect

```csharp
var prepared = await scope.Run(PrepareOrder(quantity));
return await scope.Run(ConfirmOrder(prepared));
```

`scope.Get` reads a `Result` that already exists. `await scope.Run(effect)` executes an effect inside the current Gen block and gives the block its successful value. Here `PrepareOrder` produces an `int`, which becomes the argument to `ConfirmOrder`. The confirmation produces a `string`, which becomes the success value of `PlaceOrder`.

Both steps use `string` for expected errors and `EmptyEnvironment` for the environment, matching the outer scope in this example. The console program calls `RunAsync` once per order to execute the composed effect and get its final `Result`.

## Stop when a step fails

For quantity `0`, `ValidateQuantity` returns an `Err`. The `scope.Get` inside `PrepareOrder` ends that step with the error. The outer `scope.Run` propagates it, so `ConfirmOrder` is skipped. The caller receives the original validation message in an `Err<string>`.

You can tell which steps ran by their messages. Only the valid order prints `Preparing the order.` and `Confirming the order.`.

## Try it

1. Replace `2` with `3`. Check that the confirmation includes the quantity passed from the first step.
2. Run the invalid quantity first. Check that its error is printed before either step's message.
3. Make both quantities invalid. Predict how many confirmation messages will appear, then run the program.

## Next

In the [next chapter](/tutorial/05-managing-dependencies/), give the order preparation effect a dependency and choose which implementation it uses at run time.
