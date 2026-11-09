---
title: From Results to Effects
---

# From Results to Effects

[Moderato.Results](https://github.com/SingUp009/Moderato.Results) represents an outcome as `Result<T, E>`. Effects use those same `Ok<T>` and `Err<E>` cases at the run boundary, and add a description of work with explicit dependencies.

## Lazy execution

This program increments the counter twice:

```csharp
using Moderato.Effects;
using Moderato.Results;

var reads = 0;
var read = Effect<EmptyEnvironment>.Sync(() => ++reads);
var doubled = read.Map(value => value * 2);

Console.WriteLine(reads);                      // 0
Console.WriteLine(await doubled.RunAsync());    // Ok containing 2
Console.WriteLine(await doubled.RunAsync());    // Ok containing 4
```

`Map` transforms a successful value. Creating the effect does not increment `reads`; each run does. A `Result` already contains an outcome. An ordinary async method starts executing when called; an Effect starts executing when you run it.

## Error handling

The following is an example using a typed error:

```csharp
using Moderato.Effects;
using Moderato.Results;

Effect<int, string, EmptyEnvironment> parse =
    Effect<EmptyEnvironment>.FromResult<int, string>(
        int.TryParse("21", out var value)
            ? new Ok<int>(value)
            : new Err<string>("not a number"));

var result = await parse
    .Map(value => value * 2)
    .MapErr(error => $"Invalid input: {error}")
    .RunAsync();

Console.WriteLine(result switch
{
    Ok<int>(var answer) => answer.ToString(),
    Err<string>(var error) => error,
});
```

This prints `42`. Change `"21"` to `"hello"` to take the Err path. Parsing here happens when building the Result; wrap work in `Sync`, `Try` or a Gen block when it should happen on each execution.

| Operation | Meaning in both libraries |
|---|---|
| Map | Transform Ok; leave Err unchanged |
| MapErr | Transform Err; leave Ok unchanged |
| AndThen | Continue on Ok; short-circuit on Err |
| OrElse | Recover from Err |
| Match | Turn either case into a value |
| Tap / TapErr | Observe a case |

An effect-returning `AndThen` callback is awaited as part of execution. You do not need a separate `AndThenAsync` operator: the description already supports synchronous and asynchronous work. Use [Gen blocks](/gen-blocks/) when control flow is clearer as ordinary async code.

## `Effect` or `Result` ?

Use `Result<T, E>` when:

- The outcome is already known, for example after parsing a string or validating input in memory.
- No services, cleanup or concurrency are involved.
- You are writing a pure transformation that a caller will embed in a larger effect.

Use `Effect<T, E, R>` when:

- The work must be deferred and described before it runs.
- You need composable dependencies (`R`), resource cleanup, retries, timeouts or managed concurrency.
- You want lazy re-execution: each `RunAsync` call starts the work from scratch.

An `Effect<T, E, R>` is not a promise that exceptions cannot occur. `E` lists expected errors; unexpected exceptions become Die, and interruption is separate. `RunAsync` returns `Result<T, E>` and throws those abnormal outcomes. `RunExitAsync` exposes all four outcomes as values. See [Failures](/failures/).
