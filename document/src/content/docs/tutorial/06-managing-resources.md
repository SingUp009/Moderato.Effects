---
title: 06 - Managing Resources
description: Acquire a writer with Resource.Make and scope.Acquire, then observe its release on success and expected failure.
---

# Managing Resources

In [chapter 5](../05-managing-dependencies/), the caller supplied a `TextWriter` and disposed of it when it was finished. Now the effect opens its own writer for an order draft. The writer needs to be closed even when order preparation ends with an expected error.

## Run the chapter

From the repository root, run the example.

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 06
```

The completed code is in [06_ManagingResources.cs](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/06_ManagingResources.cs).

## Build it yourself

Replace `Program.cs` in your tutorial console project with the following program.

```csharp
using Moderato.Effects;
using Moderato.Results;

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
```

Run `dotnet run` to see the order in which things happen.

```text
Opening order draft.
Closed order draft.
Order draft (quantity: 2).
Opening order draft.
Closed order draft.
Order rejected: Quantity must be greater than zero.
```

In each run, the writer closes before the caller prints the result. On the second run, the invalid quantity ends the block before it writes the draft. The writer has already been opened, so it still needs to close.

## Pair acquisition with release

`Resource.Make` pairs two operations. The `acquire` effect creates the writer. `Effect<EmptyEnvironment>.Sync` wraps the synchronous code, and the lambda creates a fresh writer whenever the resource is acquired. The `release` callback receives that writer and returns the effect that disposes of it.

The inferred type is `Resource<StringWriter, Never, EmptyEnvironment>`. The resource supplies a `StringWriter`, its acquisition declares no expected error, and it needs no services from the caller. The release returns `Unit.Value` because its purpose is to dispose of the writer. The callback also receives an `ExitCase` that describes how use of the resource ended. This example discards it with `_` because it disposes of the writer in every case.

## Acquire the writer inside the scope

```csharp
var writer = await scope.Acquire(draftWriter);
```

`scope.Acquire` runs the acquisition effect and registers the release for when the `Gen` block ends. The block can use the writer until then. On success it copies the draft to a string, which the caller can use after the writer has been disposed.

When the quantity is invalid, `throw scope.Fail(...)` ends the block with an expected error. The scope runs the registered release before `RunAsync` returns that error. The same scope also releases acquired resources if an unexpected exception or cancellation ends the block. See [Resources](/resources/) for release order and cleanup when more than one resource is involved.

## Try it

1. Run the chapter with two positive quantities. Confirm that each run opens and closes its own writer.
2. Move `scope.Acquire` below the quantity check. On the invalid run, confirm that neither the opening nor the closing message appears. Cleanup runs for resources that were acquired.

## Next

In [chapter 7](../07-concurrent-effects/), run two independent effects together and see how the group waits for cleanup when one fails.
