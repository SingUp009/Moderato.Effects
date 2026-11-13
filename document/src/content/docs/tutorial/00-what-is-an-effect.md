---
title: 00 - What is an Effect?
description: Learn how pure computations, side effects, and context lead to typed descriptions of work in C#.
---

# What is an "Effect"?

What can you tell about a calculation before you run it? A C# signature tells you what arguments to pass and what type of value to expect. To understand the whole operation, you may also need to know what it does to the world around it, what it needs from that world, and how it can fail.

We'll start with small C# methods and build up to reading the type of an asynchronous operation.

## Start with a pure function

```csharp
static bool IsEven(int number) => number % 2 == 0;
```

`IsEven(2)` evaluates to `true`. Wherever the program uses this call, you could substitute `true` and observe the same behavior. The method makes its decision from its argument and returns it. It leaves the rest of the program alone.

This property is called *referential transparency*. For a pure computation like this one, you can reason about the expression by reasoning about its value. When you test the method, choosing the argument is enough to determine the answer.

## The same return value can hide different behavior

Suppose you add a log message.

```csharp
static bool IsEven(int number)
{
    Console.WriteLine($"Checking {number}");
    return number % 2 == 0;
}
```

The call still returns `true` for `2`. If you replace the call with `true`, the message disappears. If you call it twice, it prints twice. The result is predictable in both versions, but the second version also performs an observable action. Writing the message is a *side effect*.

Now consider a method that only reads from its surroundings.

```csharp
static bool HasExpired(DateTimeOffset expiresAt) =>
    DateTimeOffset.UtcNow >= expiresAt;
```

The same expiration date can yield `false` now and `true` later. The current time is another input to the decision, even though it does not appear among the parameters. You can separate the comparison from the clock reading by making the input explicit.

```csharp
static bool HasExpired(DateTimeOffset expiresAt, DateTimeOffset now) =>
    now >= expiresAt;
```

Now a test can choose a time just before expiration and another exactly at expiration. In production, the caller still needs a clock. The change gives the code that makes the decision and the code that reads the clock separate jobs.

## How an effect system describes a computation

A type system can tell us that an expression produces a Boolean. An effect system can also track which kinds of operations may happen when the expression is evaluated. We can write both facts in a single *typing judgment*.

<div class="math-block not-content">
  <math display="block">
    <mrow><mi>Γ</mi><mo>⊢</mo><mi>e</mi><mo>:</mo><mi>T</mi><mo>▷</mo><mi>ε</mi></mrow>
  </math>
</div>

Read this as “under the assumptions in Γ, expression e has result type T and may perform effects in ε.” Γ (gamma) records what we know about the names in scope. For example, it can say that `number` is an `int`. The symbol ⊢ separates those assumptions from what we can conclude. Here ε (epsilon) is a set of effect labels. We will track two operations in this small model, writing to the console and reading the clock.

The two versions of the evenness check have the same result type. Inside their bodies, the expressions have different effect annotations.

<div class="math-block not-content">
  <math display="block">
    <mrow>
      <mi>number</mi><mo>:</mo><mi>int</mi><mo>⊢</mo><mtext>number % 2 == 0</mtext>
      <mo>:</mo><mi>bool</mi><mo>▷</mo><mi>∅</mi>
    </mrow>
  </math>
  <math display="block">
    <mrow>
      <mi>number</mi><mo>:</mo><mi>int</mi><mo>⊢</mo><mtext>Console.WriteLine(…); number % 2 == 0</mtext>
      <mo>:</mo><mi>bool</mi><mo>▷</mo><mo>{</mo><mi>ConsoleWrite</mi><mo>}</mo>
    </mrow>
  </math>
</div>

The empty set ∅ means that the first expression performs no tracked operation. The second can write to the console before returning a Boolean.

### Combine effects when expressions are sequenced

Suppose `e₁; e₂` means “evaluate e₁, discard its value, then evaluate e₂.” In this model, the sequence can perform any effect of either expression. We express that with set union (∪).

<div class="math-block not-content">
  <math display="block">
    <mfrac>
      <mrow>
        <mi>Γ</mi><mo>⊢</mo><msub><mi>e</mi><mn>1</mn></msub>
        <mo>:</mo><msub><mi>T</mi><mn>1</mn></msub><mo>▷</mo><msub><mi>ε</mi><mn>1</mn></msub>
        <mspace width="1.5em"></mspace>
        <mi>Γ</mi><mo>⊢</mo><msub><mi>e</mi><mn>2</mn></msub>
        <mo>:</mo><msub><mi>T</mi><mn>2</mn></msub><mo>▷</mo><msub><mi>ε</mi><mn>2</mn></msub>
      </mrow>
      <mrow>
        <mi>Γ</mi><mo>⊢</mo><msub><mi>e</mi><mn>1</mn></msub><mo>;</mo><msub><mi>e</mi><mn>2</mn></msub>
        <mo>:</mo><msub><mi>T</mi><mn>2</mn></msub><mo>▷</mo>
        <mo>(</mo><msub><mi>ε</mi><mn>1</mn></msub><mo>∪</mo><msub><mi>ε</mi><mn>2</mn></msub><mo>)</mo>
      </mrow>
    </mfrac>
  </math>
</div>

The judgments above the line are the premises. If both hold, we can derive the judgment below the line. For example, writing to the console and then reading the current time has the effect set `{ConsoleWrite, ClockRead}` and returns a `DateTimeOffset`. If the write is conditional, `ConsoleWrite` still appears in the set because some execution paths can reach it.

Throwing an exception can also affect evaluation by interrupting the normal flow of a computation. An effect system can track that possibility too.

## What does a task tell us about failure?

Consider the signature of an asynchronous method that loads a profile.

```csharp
Task<Profile> GetUserProfileAsync(int id)
```

The signature says to pass an ID and expect a task that produces a `Profile` on success. Suppose the method queries a database over the network. The database can have no row for that ID, and the connection can fail before the query returns. If the method signals both cases with exceptions, awaiting the task can throw. The signature gives the caller no names for those outcomes.

### Expected and unexpected failures

A missing user is an *expected failure*. The caller can tell the user that the profile was not found. An unavailable database can also be an expected failure for this operation. The caller might show a temporary outage message and let the user try again. In both cases, the caller has a meaningful next step even though the lookup failed.

An accidental null dereference is an *unexpected failure*. The lookup cannot carry out its intended work, and the application may need to report the bug and abandon the current request. What counts as expected depends on the operation and what its caller can reasonably handle.

### What happens when both are exceptions?

If the lookup throws in the two expected cases, the caller can handle them with `try` and `catch`. Here is one way that caller might look.

```csharp
try
{
    var profile = await GetUserProfileAsync(id);
    ShowProfile(profile);
}
catch (UserNotFoundException)
{
    ShowMessage("No profile found.");
}
catch (RepositoryUnavailableException)
{
    ShowMessage("Profiles are temporarily unavailable.");
}
```

Loading and displaying the profile fit in two lines inside the `try` block. The responses we planned for live in exception handlers. An unexpected exception from that same block travels through the same mechanism. A broad `catch` would receive all three kinds of exception and have to distinguish the planned responses from the bug.

An exception with no matching handler propagates up to the caller. If it escapes the program's entry point, it can end the program. Should an ordinary missing profile or a temporary connection failure be able to take that path?

### Return expected failures as values

If the caller already has a response for a failure, we can make it part of the method's return type.

```csharp
Task<Result<Profile, AppError>> GetUserProfileAsync(int id)
```

Awaiting the task can now give the caller either a successful `Profile` or an expected error such as `NotFound` or `RepositoryUnavailable`. The caller can inspect those outcomes in ordinary code. Unexpected exceptions can still propagate through the task and be handled at the application's boundary.

`Task<Result<Profile, AppError>>` tells the caller which outcomes to handle. We can also make the services needed for the lookup visible in its declaration.

## Read an effect's type

Suppose the profile includes the time it was loaded. The operation finds the user through a repository, then reads a clock to build the profile. Here is the signature in Moderato.Effects.

```csharp
static Effect<Profile, AppError, R> GetUserProfile<R>(int id)
    where R : IHas<IUserRepository>, IHas<TimeProvider>
```

| Part of the declaration | What the caller learns |
| --- | --- |
| `Profile` | The operation produces a profile on success. |
| `AppError` | The operation reports expected failures through this type. Its cases in this example are `NotFound` and `RepositoryUnavailable`. |
| `R` and the `where` clause | The environment supplied at execution must provide an `IUserRepository` and a `TimeProvider`. |

In the typing judgment above, `ClockRead` describes an operation that evaluation may perform. In this signature, `IHas<TimeProvider>` says that the environment must provide the clock the operation can use. The lookup may end before it reaches the clock, so think of the signature as telling you which services to have available when the work starts.

In production, the environment can supply a database-backed repository and the system clock. In a test, it can supply a repository with a known user and a clock set to a chosen time.

## Further reading

See [Why Effect?](https://effect.website/docs/v4/getting-started/why-effect) for the motivation in TypeScript.

For a language with effect annotations and formal typing rules, see [Koka: Programming with Row-Polymorphic Effect Types](https://www.microsoft.com/en-us/research/publication/koka-programming-with-row-polymorphic-effect-types-2/).

## Next

In [chapter 1](../01-results/), you'll write a method that returns a `Result` and handle both of its cases.
