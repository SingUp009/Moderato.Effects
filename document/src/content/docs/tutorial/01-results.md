---
title: 01 - Result
description: Validate an order quantity with Moderato.Results and handle success and expected failure explicitly in a C# console application.
---

# Success and Failure with Result

Before writing an effect, start with the result it will produce. In this chapter, you will validate an order quantity and handle both success and failure as values.

## Run the first program

Create a console project and add [Moderato.Results](https://github.com/SingUp009/Moderato.Results):

```sh
dotnet new console --name Moderato.Effects.Tutorial --framework net11.0
cd Moderato.Effects.Tutorial
dotnet add package Moderato.Results
```

Moderato.Effects uses this library's `Result<T, E>` for its execution results. This chapter uses it directly.

Replace `Program.cs` with:

```csharp
using Moderato.Results;

foreach (var quantity in new[] { 2, 0, -1 })
{
    Result<int, string> result = ValidateQuantity(quantity);

    Console.WriteLine(result switch
    {
        Ok<int>(var accepted) => $"Accepted quantity: {accepted}",
        Err<string>(var error) => $"Invalid quantity: {error}",
    });
}

static Result<int, string> ValidateQuantity(int quantity)
{
    if (quantity <= 0)
        return new Err<string>("Quantity must be greater than zero.");
    else
        return new Ok<int>(quantity);
}
```

Run `dotnet run`. The output is:

```text
Accepted quantity: 2
Invalid quantity: Quantity must be greater than zero.
Invalid quantity: Quantity must be greater than zero.
```

The method accepts a positive quantity. Zero and negative quantities are expected failures, returned to the caller as values.

## Read the return type

```csharp
static Result<int, string> ValidateQuantity(int quantity)
```

`Result<T, E>` represents either a successful value or an expected error:

| Type parameter | Meaning | In this program |
|---|---|---|
| `T` | The successful value's type | `int`: the accepted quantity |
| `E` | The expected error's type | `string`: a validation message |

The return type tells the caller what it can receive. A result contains one case: `Ok<T>` or `Err<E>`.

## Return success or failure

The method returns an error when the quantity is not positive:

```csharp
return new Err<string>("Quantity must be greater than zero.");
```

Otherwise, it returns the accepted quantity:

```csharp
return new Ok<int>(quantity);
```

`Ok<int>` and `Err<string>` both convert implicitly to `Result<int, string>`. The caller receives the same return type on either path.

This validation failure does not throw an exception. It is an outcome the method expects and the caller can handle. Returning a `Result` does not automatically catch exceptions from other code.

## Handle the result

The caller decides what to do with each case:

```csharp
Console.WriteLine(result switch
{
    Ok<int>(var accepted) => $"Accepted quantity: {accepted}",
    Err<string>(var error) => $"Invalid quantity: {error}",
});
```

The `switch` expression matches the result's case. The pattern `Ok<int>(var accepted)` extracts the successful quantity; `Err<string>(var error)` extracts the error message. Each arm produces the string to display.

Validation and presentation are separate: `ValidateQuantity` returns a result, and its caller chooses how to display it.

## Try it

Before running each change, predict which case the method will return:

1. Change the inputs to `1`, `5` and `10`. Each should produce `Ok<int>` with the same quantity.
2. Try `0` and `-5`. Both should produce `Err<string>` with the validation message.
3. Change the error message in `ValidateQuantity`. Only the failure output should change.

You can now write a method that returns a `Result` and handle both its success and failure cases.

## Next

A `Result` contains an outcome that has already been produced. Next, you will use `Effect.Gen` to describe the work that produces one, then execute that work with `RunAsync`.
