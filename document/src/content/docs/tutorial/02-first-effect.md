---
title: 02 - Your First Effect
description: Turn order validation into a deferred C# effect with Effect.Gen, run it with RunAsync, and read its typed result.
---

# Your First Effect

In [chapter 1](../01-results/), you returned a `Result` from a method. Calling the method ran the validation immediately. An effect lets you describe work first and choose when to run it.

In this chapter, you will write an effect with `Effect.Gen`, execute it and read its result. You need the console project from chapter 1 and basic `async` / `await`: an `async` method or lambda can use `await` to wait for an asynchronous operation to finish.

## Run the chapter

From the repository root, run:

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 02
```

The completed code is in [02_FirstEffect.cs](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/02_FirstEffect.cs).

## Build it yourself

From the console project's directory, add Moderato.Effects:

```sh
dotnet add package Moderato.Effects
```

Keep the Moderato.Results reference from chapter 1. Moderato.Effects uses the same result types.

Replace `Program.cs` with:

```csharp
using Moderato.Effects;
using Moderato.Results;

var prepareOrder = Effect.Gen(async (Scope<Never, EmptyEnvironment> scope) =>
{
    await Console.Out.WriteLineAsync("Preparing the order.");
    return "Order ready.";
});

Console.WriteLine("Effect created.");

Result<string, Never> result = await prepareOrder.RunAsync();

if (result is Ok<string>(var message))
    Console.WriteLine(message);
```

Run `dotnet run`. The output is:

```text
Effect created.
Preparing the order.
Order ready.
```

Creating `prepareOrder` does not print the preparation message. The block runs when you call `RunAsync`.

This program only prints messages; it does not save an order.

## Describe the work with Gen

```csharp
var prepareOrder = Effect.Gen(async (Scope<Never, EmptyEnvironment> scope) =>
{
    await Console.Out.WriteLineAsync("Preparing the order.");
    return "Order ready.";
});
```

`Effect.Gen` creates an effect from the lambda. The block contains ordinary C#: here, it awaits a console write and returns a string. The returned string becomes the successful value when the effect runs.

The parameter is a `Scope<E, R>`. A scope gives a Gen block access to operations such as running other effects; you will use those operations in later chapters. This block does not need to call any scope methods.

For now, read the two type arguments as:

| Type | Meaning in this block |
|---|---|
| `Never` | There are no expected errors to return |
| `EmptyEnvironment` | No services need to be supplied to run the effect |

Writing the parameter's type lets `Effect.Gen` infer the error and environment types. It infers the successful value's type from `return`.

## Execute the effect

```csharp
Result<string, Never> result = await prepareOrder.RunAsync();
```

`RunAsync` starts the described work. Awaiting it gives you a `Result`, the same type you handled in chapter 1.

Because the environment is `EmptyEnvironment`, you do not pass any services to `RunAsync`. This effect has no expected error, so the program reads its successful case:

```csharp
if (result is Ok<string>(var message))
    Console.WriteLine(message);
```

The `is` pattern checks for `Ok<string>` and extracts its value into `message`. The block returns `"Order ready."`, so that is the value printed here. You return the string itself from the Gen block; execution puts it in the successful result.

`Never` describes expected errors only. Unexpected exceptions and cancellation can still make `RunAsync` throw; they are covered separately in [Failures](/failures/).

## Read the effect's type

The inferred type of `prepareOrder` is:

```csharp
Effect<string, Never, EmptyEnvironment>
```

| Type parameter | Meaning | In this program |
|---|---|---|
| `T` | The successful value's type | `string`: the order message |
| `E` | The expected error's type | `Never`: no expected errors |
| `R` | The environment needed to run the work | `EmptyEnvironment`: no services to supply |

A `Result<string, Never>` contains an outcome. An `Effect<string, Never, EmptyEnvironment>` describes work that will produce one when executed.

## Run it again

Append this line to the end of `Program.cs`:

```csharp
await prepareOrder.RunAsync();
```

The output becomes:

```text
Effect created.
Preparing the order.
Order ready.
Preparing the order.
```

Each call runs the block again. The effect does not cache its result. The last line does not print `Order ready.` because this time you do not display the returned result.

## Try it

Before running each change, predict the output:

1. Remove both `RunAsync` calls and the code that reads `result`. Only `Effect created.` should be printed.
2. Restore the original program and change the string after `return`. Only the final message should change.
3. Move `Console.WriteLine("Effect created.");` below the first `RunAsync` call. The preparation message should now appear first.

You can now describe work with `Effect.Gen`, execute it and read its successful result.

## Next

Chapter 1 returned either `Ok` or `Err`. This chapter described work with no expected errors. Next, you will bring the two together: use the quantity validation result inside a Gen block and let an expected failure stop the work.
