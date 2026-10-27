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

static void AssertZeroAllocation<T, E, R>(Effect<T, E, R> effect, R environment, Result<T, E> expected)
{
    for (var i = 0; i < 1024; i++) effect.RunAsync(environment).GetAwaiter().GetResult();
    Result<T, E> last = default;
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 4096; i++)
    {
        var task = effect.RunAsync(environment);
        if (!task.IsCompletedSuccessfully) throw new InvalidOperationException("Expected synchronous completion.");
        last = task.GetAwaiter().GetResult();
    }

    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
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
