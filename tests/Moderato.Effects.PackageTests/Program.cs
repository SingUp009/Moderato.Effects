using Moderato.Effects;
using Moderato.Results;

// Uses the package as an application does, under both JIT and Native AOT.
var env = new AppEnv(new Users(), TimeProvider.System);

var found = await Profiles<AppEnv>.Find(1).Timeout(TimeSpan.FromSeconds(5)).RunAsync(env);
var missing = await Profiles<AppEnv>.Find(2).Timeout(TimeSpan.FromSeconds(5)).RunAsync(env);
var exit = await Profiles<AppEnv>.Find(3).RunExitAsync(env);
var retried = await Profiles<AppEnv>.Find(2).Retry(Schedule.Immediately.UpTo(1)).RunAsync(env);
var repeated = await Profiles<AppEnv>.Find(1).Repeat(Schedule.Immediately.UpTo(1)).RunAsync(env);
var slept = await Effect.Sleep<AppEnv>(TimeSpan.Zero).RunAsync(env);

// Exercise constrained service access with a struct under both JIT and Native AOT.
var valueClock = default(ValueClock);
var valueSuccess = Effect<ValueClock>.FromResult<int, string>(Result.Ok(20));
var valueFailure = Effect<ValueClock>.FromResult<int, string>(Result.Err("failed"));
AssertZeroAllocation(valueFailure.Retry(Schedule.Immediately.UpTo(7)), valueClock, Result.Err("failed"));
AssertZeroAllocation(valueSuccess.Repeat(Schedule.Immediately.UpTo(7)), valueClock, Result.Ok(20));
AssertZeroAllocation(Effect.Sleep<ValueClock>(TimeSpan.Zero), valueClock, Result.Ok(Unit.Value));
await AssertZeroTimeout();
await AssertUnionMemberProvider();
await AssertInterruptedRelease(useResource: false);
await AssertInterruptedRelease(useResource: true);
await AssertSnapshotTransfer();
await AssertImplicitUnionWidening();

string[] expected = ["Ok(ada)", "Err(NotFound 2)", "Die(InvalidOperationException)", "Err(NotFound 2)", "Ok(ada)", "Sleep(Ok)"];
string[] actual =
[
    found.Match(user => $"Ok({user})", error => $"Err({error.Value})"),
    missing.Match(user => $"Ok({user})", error => error.Value is NotFound n ? $"Err(NotFound {n.Id})" : "Err(?)"),
    exit.TryGetValue(out Die? die) ? $"Die({die.Exception.GetType().Name})" : exit.ToString() ?? "",
    retried.Match(user => $"Ok({user})", error => error.Value is NotFound n ? $"Err(NotFound {n.Id})" : "Err(?)"),
    repeated.Match(user => $"Ok({user})", error => $"Err({error.Value})"),
    slept.TryGetOk(out _) ? "Sleep(Ok)" : "Sleep(Err)",
];

for (var i = 0; i < expected.Length; i++)
{
    if (expected[i] == actual[i]) continue;
    Console.Error.WriteLine($"expected {expected[i]}, got {actual[i]}");
    return 1;
}

Console.WriteLine("package works: " + string.Join(", ", actual));
return 0;

static async Task AssertImplicitUnionWidening()
{
    foreach (var payload in new object?[] { null, new object() })
    {
        // Given
        Effect<int, object?, EmptyEnvironment> source = Effect<EmptyEnvironment>.Fail<object?>(payload);

        // When
        var valueUnion = await source.Widen<ObjectError>()
            .CatchCase((object? value) => Effect<EmptyEnvironment>.Succeed(ReferenceEquals(payload, value) ? 42 : -1)).RunAsync(CancellationToken.None);
        var referenceUnion = await source.Widen<ObjectClassError>()
            .CatchCase((object? value) => Effect<EmptyEnvironment>.Succeed(ReferenceEquals(payload, value) ? 42 : -1)).RunAsync(CancellationToken.None);

        // Then
        if (valueUnion.Value is not Ok<int> { Value: 42 } || referenceUnion.Value is not Ok<int> { Value: 42 })
            throw new InvalidOperationException("Object cases must create intermediate unions and retain null or the original payload.");
    }

    // Given
    ILeaf payloadLeaf = new LeafPayload();
    Effect<int, ILeaf, EmptyEnvironment> failed = Effect<EmptyEnvironment>.Fail(payloadLeaf);
    ValueType boxedValue = 42;
    Effect<int, ValueType, EmptyEnvironment> valueFailure = Effect<EmptyEnvironment>.Fail(boxedValue);
    System.Runtime.CompilerServices.IUnion boxedUnion = new ObjectCarrier("payload");
    Effect<int, System.Runtime.CompilerServices.IUnion, EmptyEnvironment> unionFailure = Effect<EmptyEnvironment>.Fail(boxedUnion);

    // When
    var widened = await failed.Widen<InterfaceError>()
        .CatchCase((ILeaf value) => Effect<EmptyEnvironment>.Succeed(ReferenceEquals(payloadLeaf, value) ? 42 : -1)).RunAsync(CancellationToken.None);
    var remaining = await failed.Widen<InterfaceSource>()
        .Catch<Guid, InterfaceRemaining>(_ => Effect<EmptyEnvironment>.Succeed(0)).RunAsync(CancellationToken.None);
    var valueCase = await valueFailure.Widen<ValueTypeError>()
        .CatchCase((ValueType value) => Effect<EmptyEnvironment>.Succeed(ReferenceEquals(boxedValue, value) ? 42 : -1)).RunAsync(CancellationToken.None);
    var unionCase = await unionFailure.Widen<UnionInterfaceError>()
        .CatchCase((System.Runtime.CompilerServices.IUnion value) => Effect<EmptyEnvironment>.Succeed(ReferenceEquals(boxedUnion, value) ? 42 : -1)).RunAsync(CancellationToken.None);

    // Then
    if (widened.Value is not Ok<int> { Value: 42 }
        || valueCase.Value is not Ok<int> { Value: 42 } || unionCase.Value is not Ok<int> { Value: 42 }
        || remaining.Value is not Err<InterfaceRemaining> error || error.Error is not LeafCarrier carrier
        || carrier is not ILeaf leaf || !ReferenceEquals(payloadLeaf, leaf))
    {
        throw new InvalidOperationException("Interface cases must create intermediate unions in both widening and splitting.");
    }
}

static async Task AssertInterruptedRelease(bool useResource)
{
    // Given
    Fiber<Unit, Never, DefaultEnvironment>? kept = null;
    await Effect.Sleep<DefaultEnvironment>(Timeout.InfiniteTimeSpan).Fork().Use(fiber =>
    {
        kept = fiber;
        return Effect<DefaultEnvironment>.Succeed(Unit.Value);
    }).RunAsync();
    var closed = await kept!.Join().RunExitAsync();
    if (closed.Value is not Interrupted)
        throw new InvalidOperationException("The release input must be an interrupted child.");
    var remaining = 0;
    Effect<Unit, Never, DefaultEnvironment> finish = Effect<DefaultEnvironment>.Sync(() => { remaining++; return Unit.Value; });
    Effect<int, Never, DefaultEnvironment> effect;
    if (useResource)
    {
        var outer = Resource.Make(Effect<DefaultEnvironment>.Succeed(Unit.Value), (_, _) => finish);
        var inner = Resource.Make(Effect<DefaultEnvironment>.Succeed(42), (_, _) => kept!.Join());
        effect = outer.AndThen(_ => inner).Use(static value => Effect<DefaultEnvironment>.Succeed(value));
    }
    else
    {
        effect = Scope<Never, DefaultEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(_ => finish);
            scope.Defer(_ => kept!.Join());
            return new(42);
        });
    }

    // When
    var exit = await effect.RunExitAsync();

    // Then
    if (remaining != 1 || exit.Value is not Die { Exception: InvalidOperationException })
        throw new InvalidOperationException("Interrupted cleanup must become a defect after every remaining release runs.");
}

static async Task AssertSnapshotTransfer()
{
    // Given
    var completion = new TaskCompletionSource<Result<int, string>>();
    var defect = new InvalidOperationException("after snapshot");
    Task<int>? transferred = null;
    CancellationTokenRegistration registration = default;
    var effect = Scope<string, EmptyEnvironment>.Gen<int>(scope =>
    {
        scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() => { registration.Dispose(); return Unit.Value; }));
#pragma warning disable MEFF310
        var step = scope.Run(Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(completion.Task)));
#pragma warning restore MEFF310
        transferred = step.AsTask();
        registration = scope.CancellationToken.Register(() => completion.SetException(defect));
        return new(42);
    });

    // When
    var exit = await effect.RunExitAsync();

    // Then
    if (exit.Value is not Die die || !ReferenceEquals(defect, die.Exception) || transferred is not { IsFaulted: true })
        throw new InvalidOperationException("An adapter read after the body snapshot must not erase the captured defect.");
    try
    {
        await transferred;
        throw new InvalidOperationException("The transferred Task must retain its defect too.");
    }
    catch (InvalidOperationException exception) when (ReferenceEquals(defect, exception))
    {
    }
}

static async Task AssertUnionMemberProvider()
{
    // Given
    Effect<int, string, EmptyEnvironment> failure = Effect<EmptyEnvironment>.Fail("hello");
    var defect = new InvalidOperationException("expected error");
    Effect<int, ProviderOutcome<string>, EmptyEnvironment> held = Effect<EmptyEnvironment>.Fail<ProviderOutcome<string>>("hello");
    Effect<int, ProviderOutcome<string>, EmptyEnvironment> handled = Effect<EmptyEnvironment>.Fail<ProviderOutcome<string>>(defect);
    Effect<int, object?, EmptyEnvironment> empty = Effect<EmptyEnvironment>.Fail<object?>(null);

    // When
    var widened = await failure.Widen<ProviderError>()
        .CatchCase((string value) => Effect<EmptyEnvironment>.Succeed(value.Length)).RunAsync(CancellationToken.None);
    var remaining = await held
        .Catch<Exception, ProviderRemaining>(_ => Effect<EmptyEnvironment>.Succeed(42)).RunAsync(CancellationToken.None);
    var caught = await handled
        .Catch<Exception, ProviderRemaining>(_ => Effect<EmptyEnvironment>.Succeed(42)).RunAsync(CancellationToken.None);
    var nullCase = await empty.Widen<ProviderAliasError>()
        .CatchCase((object? value) => Effect<EmptyEnvironment>.Succeed(value is null ? 42 : -1)).RunAsync(CancellationToken.None);

    // Then
    if (widened.Value is not Ok<int> { Value: 5 }
        || remaining.Value is not Err<ProviderRemaining> error || error.Error is not string text || text != "hello"
        || caught.Value is not Ok<int> { Value: 42 }
        || nullCase.Value is not Ok<int> { Value: 42 })
    {
        throw new InvalidOperationException("Provider cases must support widening, matching and splitting.");
    }
}

static async Task AssertZeroTimeout()
{
    // Given
    var calls = 0;
    var source = Effect<ValueClock>.Sync(() => ++calls);
    Effect<int, TimeoutError, ValueClock> existing = source;
    Effect<int, AppError, ValueClock> union = source;
    Effect<int, string, ValueClock> mapped = source;

    // When
    var directExit = await source.Timeout(TimeSpan.Zero).RunExitAsync(default(ValueClock));
    var existingExit = await existing.Timeout(TimeSpan.Zero).RunExitAsync(default(ValueClock));
    var unionExit = await union.Timeout(TimeSpan.Zero).RunExitAsync(default(ValueClock));
    var mappedExit = await mapped.Timeout(TimeSpan.Zero, static _ => "expired").RunExitAsync(default(ValueClock));
    var resultExit = await source.TimeoutResult(TimeSpan.Zero).RunExitAsync(default(ValueClock));

    // Then
    if (calls != 0
        || directExit.Value is not Err<TimeoutError> directError || directError.Error.After != TimeSpan.Zero
        || existingExit.Value is not Err<TimeoutError> existingError || existingError.Error.After != TimeSpan.Zero
        || unionExit.Value is not Err<AppError> unionError || unionError.Error.Value is not TimeoutError { After: var after } || after != TimeSpan.Zero
        || mappedExit.Value is not Err<string> { Error: "expired" }
        || resultExit.Value is not Ok<Result<int, TimeoutError>> result || result.Value.Value is not Err<TimeoutError> resultError || resultError.Error.After != TimeSpan.Zero)
    {
        throw new InvalidOperationException("A zero timeout must expire without running its body.");
    }
}

static void AssertZeroAllocation<T, E, R>(Effect<T, E, R> effect, R environment, Result<T, E> expected)
{
    for (var i = 0; i < 1024; i++) effect.RunAsync(environment).GetAwaiter().GetResult();
    Result<T, E> last = default;
    var allocated = long.MaxValue;
    // A one-off runtime allocation can land in one window, while one made by every run lands in all of them.
    for (var window = 0; window < 3 && allocated != 0; window++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++)
        {
            var task = effect.RunAsync(environment);
            if (!task.IsCompletedSuccessfully) throw new InvalidOperationException("Expected synchronous completion.");
            last = task.GetAwaiter().GetResult();
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    }

    if (last != expected || allocated != 0)
    {
        throw new InvalidOperationException($"Expected {expected} and 0 B, got {last} and {allocated} B.");
    }
}

public readonly struct ValueClock : IHas<TimeProvider>
{
    TimeProvider IHas<TimeProvider>.Service => TimeProvider.System;
}

public record NotFound(int Id);

public union ObjectCarrier(object?);
[Error] public partial union ObjectError(ObjectCarrier);
public union ValueTypeCarrier(ValueType);
[Error] public partial union ValueTypeError(ValueTypeCarrier);
public union UnionInterfaceCarrier(System.Runtime.CompilerServices.IUnion);
[Error] public partial union UnionInterfaceError(UnionInterfaceCarrier);

[System.Runtime.CompilerServices.Union]
public sealed class ObjectClassCarrier(object? value)
{
    public object? Value => value;
}
[Error] public partial union ObjectClassError(ObjectClassCarrier);

public interface ILeaf;
public sealed class LeafPayload : ILeaf;
[System.Runtime.CompilerServices.Union]
public readonly struct LeafCarrier(ILeaf value) : ILeaf
{
    public object? Value => value;
}
[Error] public partial union InterfaceError(LeafCarrier);
[Error] public partial union InterfaceSource(ILeaf, Guid);
[Error, RestOf<InterfaceSource>] public partial union InterfaceRemaining(LeafCarrier);

[System.Runtime.CompilerServices.Union]
public partial struct ProviderOutcome<T> : ProviderOutcome<T>.IUnionMembers
{
    private readonly object? value;
    private ProviderOutcome(object? value) => this.value = value;
    public interface IFactory
    {
        static ProviderOutcome<T> Create(T value) => new(value);
        static ProviderOutcome<T> Create(Exception value) => new(value);
    }
    public interface IUnionMembers : IFactory
    {
        new static ProviderOutcome<T> Create(T value) => new(value);
        static ProviderOutcome<T> Create<U>(U value) => new(value);
        object? Value { get; }
    }
    object? IUnionMembers.Value => value;
}

[Error]
public partial union ProviderError(ProviderOutcome<string>, Guid);

[System.Runtime.CompilerServices.Union]
public struct ProviderAlias<T> : ProviderAlias<T>.IUnionMembers
{
    private readonly object? value;
    private ProviderAlias(object? value) => this.value = value;
    public interface IFactory { static ProviderAlias<T> Create(T value) => new(value); }
    public interface IUnionMembers : IFactory
    {
        static ProviderAlias<T> Create(dynamic? value) => new((object?)value);
        object? Value { get; }
    }
    object? IUnionMembers.Value => value;
}

[Error]
public partial union ProviderAliasError(ProviderAlias<object?>);

[Error, RestOf<ProviderOutcome<string>>]
public partial union ProviderRemaining(string);

[Error]
public partial union AppError(NotFound, TimeoutError);

public interface IUsers
{
    ValueTask<string?> FindAsync(int id, CancellationToken cancellationToken);
}

public sealed class Users : IUsers
{
    public ValueTask<string?> FindAsync(int id, CancellationToken cancellationToken) => id switch
    {
        1 => new("ada"),
        2 => new((string?)null),
        _ => throw new InvalidOperationException("storage failed"),
    };
}

[Environment]
public sealed partial record AppEnv([Expose<IUsers>] Users Users, TimeProvider Clock);

public static class Profiles<R> where R : IHas<IUsers>, IHas<TimeProvider>
{
    public static Effect<string, AppError, R> Find(int id) => Scope<AppError, R>.Gen(async scope =>
    {
        var name = await scope.Env.Resolve<IUsers>().FindAsync(id, scope.CancellationToken);
        return name ?? throw scope.Fail(new NotFound(id));
    });
}
