# Error unions

`E` is the set of expected errors. C# 15 unions name that set:

```csharp
public record NotFound(int Id);
public record Forbidden(string Reason);

[Error]
public partial union AppError(NotFound, Forbidden, TimeoutError);
```

`[Error]` makes the source generator implement `IUnionCase<AppError, TCase>` for each case. With it:

- `scope.Run`, `scope.Get` and `scope.Acquire` in a `Scope<AppError, R>` block accept an effect whose error is just `NotFound`.
- `effect.Widen<AppError>()` widens an `Effect<T, NotFound, R>` to `Effect<T, AppError, R>`.
- `CatchCase` and `Timeout` can find a case inside the union.

The generator only touches unions that carry the attribute. You can also implement `IUnionCase` by hand.

## Matching

`Err` holds the union, and a union pattern matches its case:

```csharp
var message = result switch
{
    Ok<Profile>(var p)              => p.Name,
    Err<AppError>(NotFound n)       => $"no user {n.Id}",
    Err<AppError>(Forbidden f)      => $"forbidden: {f.Reason}",
    Err<AppError>(TimeoutError t)   => $"timed out after {t.After}",
};
```

## Nested unions

A case can itself be a union. The generator also implements the cases of cases:

```csharp
public record ConnFailed(string Host);
public record QueryFailed(string Sql);
public union DbError(ConnFailed, QueryFailed);

[Error]
public partial union StoreError(DbError, NotFound);   // also IUnionCase<StoreError, ConnFailed> and <StoreError, QueryFailed>
```

When two paths reach the same type (for example `DbError` and `NetError` both holding `TimeoutError`), the generator reports MEFF003 and does not generate the nested paths. A direct case still works.

Recursive traversal compares constructed types: `Inner<object>` and `Inner<dynamic>`, or tuples differing only in element names, count as the same type. Different type arguments can expose different cases, so revisiting the same generic definition does not by itself stop traversal. Ordinary self/copy constructors are excluded; a self case declared by a member-provider factory remains a case.

A transitive union is a source `union` declaration or a type marked `[System.Runtime.CompilerServices.Union]`. A class or struct that only implements runtime `IUnion` is an ordinary, opaque case; its constructors do not become transitive cases.

Custom unions that use a nested `IUnionMembers` provider are supported too. Their cases come from its public static one-argument `Create` factories, including inherited factories, with no method type parameters; the union type's own constructors do not define cases in this form. See the [C# provider contract](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/union#union-member-providers).

Widening uses an implicit C# conversion at each union layer, so custom constructors and factories run as they do in handwritten C#. For runtime failure reports, see [error presentation](Failures#suppressed-failures).

## Overlapping case types

Automatic conversions need to distinguish each direct case. C# unions store their value, without retaining which constructor you selected. With cases `BaseError` and `DerivedError`, a derived value also matches the base pattern. Reordering the cases cannot make removing one of them unambiguous.

`[Error]` and `[RestOf<E>]` report MEFF008 and generate nothing for that target when different direct cases can match the same non-null value. This includes a base and derived type, an interface and an implementation, `object` beside another type, and `int` beside `int?`. Interface pairs, and an interface paired with an unsealed class, are conservatively rejected because another implementation or subclass can belong to both.

Wrap the payloads in distinct records to keep their alternatives separate:

```csharp
public record BaseError(int Code);
public sealed record DerivedError(int Code, string Detail) : BaseError(Code);
public sealed record GeneralFailure(BaseError Error);
public sealed record SpecificFailure(DerivedError Error);

[Error]
public partial union AppError(GeneralFailure, SpecificFailure);
```

The .NET runtime treats some array types as compatible: a `uint[]` object can also match an `int[]` pattern. Wrap these array payloads in distinct records too. Overlap inside separate wrapper unions does not by itself make their outer paths ambiguous.

## Handling one case

| API | Error type after | Use when |
|---|---|---|
| `CatchCase((NotFound nf) => ...)` | unchanged | you handle one case and keep the type |
| `Catch<NotFound, AccessError>(nf => ...)` | the rest, `AccessError` | the case should disappear from the type |
| `Attempt()` and `switch` | `Never` | you handle every case |
| `OrElse(e => e switch { ... })` | any `F` you choose | you map to a different error type |

### Removing a case with Catch

C# cannot compute "AppError without NotFound", so you declare it with `[RestOf<E>]`:

```csharp
[Error, RestOf<AppError>]
public partial union AccessError(Forbidden, TimeoutError);   // AppError minus NotFound

Effect<Profile, AccessError, R> orGuest =
    GetUserProfile<R>(id).Catch<NotFound, AccessError>(nf => Effect<R>.Succeed(Profile.Guest(nf.Id)));
```

Rules for a `[RestOf<E>]` union:

- It holds every case of `E` except exactly one, and no case that `E` does not have.
- The case it leaves out must be a direct case of `E`, not a case of a nested union.
- To remove two cases, chain two `Catch` calls with two rest unions.

If `F` is not a matching rest union, the compiler reports CS0315; the analyzers add a readable explanation.

## Nullable cases

A union stores only its value, so a nullable case holding `null`, such as `string?`, cannot be told apart from an uninitialized `default` union. The generator warns (MEFF005). Wrap the value in a record instead:

```csharp
public record Note(string? Text);

[Error]
public partial union NoteError(Note, int);
```

If you keep a nullable case and suppress the warning, `null`, and therefore `default`, belongs to that case when it is the only nullable one. With two or more nullable cases, `null` matches none of them.

## Limits

- Generic attributed targets and targets nested in another type are not supported. A non-generic target can contain closed generic union cases.
- The target must be a `partial union`.
- A keyword-named case type such as `@class` is supported.
- Generated code uses global type names. The generated interfaces from Moderato.Effects must also be available through the correct global names. Types available only through `extern alias`, or same-named types that make global lookup ambiguous, report MEFF009. Distinct assemblies remain distinct cases. Use an ordinary global reference, a globally accessible wrapper, or manual interfaces. A whole nested union can still be kept or removed without naming its internal cases.
- Generation and Catch analysis have a resource budget: 4,096 case-path segments per analysis and 256 nodes per type expression. A single path of 90 cases already costs 4,095 segments; additional branches use the same budget. MEFF007 stops the whole target rather than silently omitting conversions. Finite types can exceed the budget too. Simplify the graph or implement the interfaces manually.
