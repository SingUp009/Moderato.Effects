---
title: 05 - Managing Dependencies
---

# Managing Dependencies

In [chapter 4](../04-composing-effects/), order preparation wrote its progress directly to the console. In this chapter, the caller supplies a writer. The same effect can write to the console in one run and capture its output in memory in another.

## Run the chapter

From the repository root, run:

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 05
```

The completed code is in [05_ManagingDependencies.cs](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/05_ManagingDependencies.cs).

## Build it yourself

Replace `Program.cs` in your tutorial console project with:

```csharp
using Moderato.Effects;
using Moderato.Results;

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
```

Run `dotnet run`. The output is:

```text
Preparing the order.
Order ready.
Captured: Preparing the order.
```

The first run writes its progress straight to the console. The second writes into the `StringWriter`. The final `Console.Write` displays what the buffer captured.

## Declare the dependency

```csharp
var prepareOrder = Effect.Gen(async (Scope<Never, TextWriter> scope) =>
{
    await scope.Env.WriteLineAsync("Preparing the order.");
    return "Order ready.";
});
```

The environment is the `R` in `Effect<T, E, R>`. Here `R` is `TextWriter`, so the inferred type is `Effect<string, Never, TextWriter>`. The scope exposes that writer through `scope.Env`. The block can call `WriteLineAsync` on it like any other `TextWriter`.

Earlier chapters used `EmptyEnvironment` and called `RunAsync()` without an environment argument. Here `RunAsync` requires a `TextWriter`, so the compiler checks that the caller supplies one.

## Choose the writer at run time

```csharp
await prepareOrder.RunAsync(Console.Out);
await prepareOrder.RunAsync(buffer);
```

`Console.Out` and `StringWriter` both provide a `TextWriter`. Each call runs the same description with the writer supplied for that run. This also lets you check what the effect writes by passing a buffer and inspecting its contents.

The example uses a single service as its environment. See [Environments](/environments/) for effects that need several services.

## Try it

1. Run the effect twice with the same buffer. Predict what the buffer contains before printing it.
2. Change the progress message. Check that it changes in both runs.
3. Remove the writer argument from the first call to `RunAsync`. Read the compiler error, then restore the argument.

## Next

In [chapter 6](../06-managing-resources/), let the effect acquire its own writer and release it when the scope ends.
