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
