---
title: Retry and repeat
description: Build retry and repeat schedules with backoff, jitter, and limits. Decide which operations are safe to run again and how cancellation stops the schedule.
---

# Retry and repeat

Common cases use a small schedule DSL. Anything more specific is an effect you write yourself, and the most complex cases are a loop in a Gen block.

## Retry

```csharp
var result = await FetchPrices<R>()
    .Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).WithMaxDelay(TimeSpan.FromSeconds(5)).UpTo(3).Jittered(),
           when: e => e is TimeoutError)
    .RunAsync(env, ct);
```

- Only an **Err** is retried. Defects and interruptions are never retried. To retry an exception, turn it into an Err with `Effect<R>.Try` first.
- Each attempt runs the effect from the start, so the resources it acquires are released per attempt.
- When the retries run out, the last Err is the result. Chain `OrElse` to fall back to something else.
- `when:` picks which errors to retry; by default, all.

## Schedules

| API | Meaning |
|---|---|
| `Schedule.Spaced(d)` | Wait `d` each time |
| `Schedule.Exponential(base, factor = 2)` | Wait `base`, `base × factor`, `base × factor²`, … (`factor` ≥ 1) |
| `Schedule.Immediately` | Do not wait |
| `.UpTo(n)` | At most `n` retries, so at most `n + 1` attempts. For Repeat, at most `n` repetitions |
| `.During(total)` | Stop once `total` has passed since the first attempt |
| `.WithMaxDelay(max)` | Cap each wait |
| `.Jittered()` | Spread each wait randomly over 80–120% |

- A schedule says when and how often, never which errors; that is `when:`.
- Once a schedule reaches its retry or time limit, `Retry` and `Repeat` return the last result without calling `when`.
- Without `UpTo` or `During`, a schedule has no limit.
- A growing schedule caps each wait at the timer's limit (about 49.7 days) rather than failing.
- `During` checks before each wait and does not shorten the wait: `Spaced(1 hour).During(5 seconds)` can wait an hour after the first failure. For a strict limit, use `effect.Retry(schedule).Timeout(total)`.
- `WithMaxDelay` applies after jitter.

### Jitter needs a random number generator

`.Jittered()` changes the type from `Schedule` to `Schedule<IHas<Random>>`, and `Retry` then requires `R : IHas<Random>`. Without jitter, no random number constraint appears.

The library takes one seed from the environment's `Random` per run, under a lock, and uses its own `Random` from then on. A `Random` that is not thread-safe, such as `new Random(42)` in a test, stays safe under parallel retries, and a fixed seed makes sequential runs reproducible.

All schedules need `R : IHas<TimeProvider>` for waiting.

## Idempotency

`Retry` and `Repeat` run the whole effect again. The library does not make the effect's side effects safe to repeat; that is up to the effect.

- An effect that charges a card, sends an email or inserts a row does it again on every attempt.
- An Err does not mean that nothing happened. The effect may have written something and then failed.
- A timed-out attempt may also have finished its work. `Timeout` only requests cancellation (see [Time](/time/#timeouts-are-cooperative)), so retrying after `Err(TimeoutError)` can repeat a write that already went through.

Ways to keep retries safe:

- Retry only the part that is safe to repeat, such as a read or a request with an idempotency key:

```csharp
Scope<AppError, R>.Gen(async scope =>
{
    var quote = await scope.Run(FetchQuote<R>(order).Retry(schedule));    // a read: safe to retry
    return await scope.Run(Charge<R>(order, quote));                       // a write: run once
});
```

- Give a write an idempotency key that stays the same across attempts, and let the service drop duplicates. Create the key outside the retried effect, so every attempt sends the same one:

```csharp
var key = Guid.NewGuid();
var charge = Charge<R>(order, idempotencyKey: key).Retry(schedule);
```

- Retry only errors that mean "nothing happened", using `when:`, such as a connection that failed before the request was sent.

What the library does guarantee within a run:

- each acquired resource and each `Defer` action is released once, and a failing release is not retried;
- the first cancellation reason wins, and later ones are ignored;
- observed failures follow the shared [precedence and diagnostic rules](/failures/). Await every scope step, including [adapter results](/gen-blocks/#valuetask-adapters-and-failure-ownership).

## Retry with a policy

When the wait depends on the error, such as HTTP 429's `Retry-After`, write the policy as an effect:

```csharp
effect.Retry((RetryContext<AppError> ctx) => ctx.Gen<R>(async scope =>
{
    if (ctx.Attempt >= 5 || ctx.Error is not (TimeoutError or RateLimited)) return false;   // stop
    var wait = ctx.Error is RateLimited r ? r.RetryAfter : TimeSpan.FromSeconds(ctx.Attempt);
    await scope.Run(Effect.Sleep<R>(wait));
    return true;                                                                             // try again
}));
```

- The policy is an `Effect<bool, Never, R>`: `true` tries again, `false` stops. It does its own waiting.
- A policy cannot return an expected error, so its Gen block is `Scope<Never, R>`; `ctx.Gen<R>(...)` is the shorthand. It can still die or be interrupted.
- `ctx.Attempt` counts the attempts so far, the failed one included (1 after the first failure).
- If the policy itself dies or is interrupted, for example while waiting, that is the result, with the last Err as context.

## Repeat

`Repeat` runs again on **Ok**:

```csharp
var final = PollStatus<R>(jobId).Repeat(
    Schedule.Spaced(TimeSpan.FromSeconds(1)).During(TimeSpan.FromMinutes(5)),
    when: status => status is Running);
```

- An Err stops it, and that Err is the result.
- Otherwise the result is the last value, also when `During` runs out. Combine with `Timeout` to make running out a failure.
- `when:` picks which values to repeat after; by default, all.
- `Repeat` also takes a policy: `RepeatContext<T>` has `Repetition` and `Value`, and `ctx.Gen<R>(...)`.

## Cancellation

Before each policy decision and before each new attempt, the caller's token is checked; once it is canceled, the run ends as `Interrupted`. A policy that decides to stop despite a cancellation still ends with the last result.

## Beyond schedules

For anything else, write a loop in a Gen block:

```csharp
Scope<AppError, R>.Gen(async scope =>
{
    for (var attempt = 1; ; attempt++)
    {
        var result = await scope.Run(Fetch<R>().Attempt());
        if (result.TryGetOk(out var prices)) return prices;
        if (attempt == 3) return scope.Get(result);   // ends the block with the last Err
        await scope.Run(Effect.Sleep<R>(TimeSpan.FromSeconds(attempt)));
    }
});
```
