using Moderato.Effects;
using Moderato.Results;

var env = new AppEnv(new InMemoryUsers(), new Permissions(), TimeProvider.System, new ConsoleAudit());

// 1. Gen blocks: each step's error widens to AppError, and an Err stops the block.
foreach (var id in new[] { 1, 2, 3 })
{
    // Pass the token explicitly here: the .NET 11 RC2 compiler wrongly reports CS8655 on the switch below
    // when a call in a loop passes default for a CancellationToken.
    var exit = await Profiles<AppEnv>.GetUserProfile(id).RunExitAsync(env, CancellationToken.None);
    Console.WriteLine(exit switch
    {
        Ok<Profile>(var p) => $"profile: {p.Name}",
        Err<AppError>(var e) => e switch
        {
            NotFound n => $"no user {n.Id}",
            Forbidden f => $"forbidden: {f.Reason}",
            TimeoutError t => $"timed out after {t.After.TotalSeconds}s",
        },
        Die(var exception) => $"bug: {exception.Message}",
        Interrupted => "canceled",
    });
}

// 2. Catch removes one case from the error type: NotFound becomes a guest, Forbidden stays an error.
var orGuest = Profiles<AppEnv>.GetUserProfile(2).Catch<NotFound, AccessError>(nf => Effect<AppEnv>.Succeed(new Profile($"guest {nf.Id}")));
Console.WriteLine(await orGuest.RunAsync(env));

// 3. A resource is released after use, and the release sees how the use ended.
Resource<Transaction, Overdrawn, EmptyEnvironment> transaction = Bank.OpenTransaction();
var transfer = transaction.Use(tx => Bank.Transfer(tx, amount: 500));
Console.WriteLine(await transfer.RunAsync());

// 4. Concurrency, time limits and retries compose like any other operator.
var fetched = Effect.All(
        [Remote.Fetch("a", TimeSpan.FromMilliseconds(10)), Remote.Fetch("b", TimeSpan.FromMilliseconds(20))],
        Concurrency.Parallel)
    .Timeout(TimeSpan.FromSeconds(1))
    .Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(50)).UpTo(3).Jittered());
Console.WriteLine(await fetched.Map(values => string.Join(", ", values)).RunAsync());

// ---- Errors ----
public record NotFound(int Id);
public record Forbidden(string Reason);

[Error]
public partial union AppError(NotFound, Forbidden, TimeoutError);

[Error, RestOf<AppError>]
public partial union AccessError(Forbidden, TimeoutError);

// ---- Services and the environment ----
public record User(int Id, string Name);
public record Profile(string Name);

public interface IUserRepository
{
    ValueTask<User?> FindAsync(int id, CancellationToken cancellationToken);
}

public interface IPermissionService
{
    ValueTask<bool> CanViewAsync(User user, CancellationToken cancellationToken);
}

public interface IAuditLogger
{
    void Write(string message);
}

public interface IProfileDeps : IHas<IUserRepository>, IHas<IPermissionService>, IHas<TimeProvider>, IHas<IAuditLogger>;

[Environment]
public sealed partial record AppEnv(IUserRepository Users, IPermissionService Permissions, TimeProvider Clock, IAuditLogger Audit) : IProfileDeps;

public sealed class InMemoryUsers : IUserRepository
{
    public ValueTask<User?> FindAsync(int id, CancellationToken cancellationToken) => new(id is 1 or 3 ? new User(id, $"user{id}") : null);
}

public sealed class Permissions : IPermissionService
{
    public ValueTask<bool> CanViewAsync(User user, CancellationToken cancellationToken) => new(user.Id == 1);
}

public sealed class ConsoleAudit : IAuditLogger
{
    public void Write(string message) => Console.WriteLine($"  audit: {message}");
}

// ---- Effects ----
public static class Profiles<R> where R : IProfileDeps
{
    static Effect<User, NotFound, R> FindUser(int id) => Scope<NotFound, R>.Gen(async scope =>
    {
        var user = await scope.Env.Resolve<IUserRepository>().FindAsync(id, scope.CancellationToken);
        return user ?? throw scope.Fail(new NotFound(id));
    });

    static Effect<Unit, Forbidden, R> CheckAccess(User user) => Scope<Forbidden, R>.Gen(async scope =>
    {
        var allowed = await scope.Env.Resolve<IPermissionService>().CanViewAsync(user, scope.CancellationToken);
        return allowed ? Unit.Value : throw scope.Fail(new Forbidden($"user {user.Id} is private"));
    });

    static Effect<Unit, Never, R> Audit(string message) => Effect<R>.Access(env =>
    {
        env.Resolve<IAuditLogger>().Write(message);
        return Unit.Value;
    });

    public static Effect<Profile, AppError, R> GetUserProfile(int id) => Scope<AppError, R>.Gen(async scope =>
    {
        var user = await scope.Run(FindUser(id));
        await scope.Run(CheckAccess(user));
        await scope.Run(Audit($"viewed {user.Id} at {scope.Env.Resolve<TimeProvider>().GetUtcNow():HH:mm:ss}"));
        return new Profile(user.Name);
    }).Timeout(TimeSpan.FromSeconds(5));
}

public record Overdrawn(int Missing);

public sealed record Transaction(int Id);

public static class Bank
{
    public static Resource<Transaction, Never, EmptyEnvironment> OpenTransaction() => Resource.Make(
        acquire: Effect<EmptyEnvironment>.Sync(() =>
        {
            Console.WriteLine("  begin");
            return new Transaction(1);
        }),
        release: (tx, exit) => Effect<EmptyEnvironment>.Sync(() =>
        {
            Console.WriteLine(exit is Succeeded ? $"  commit {tx.Id}" : $"  rollback {tx.Id}");
            return Unit.Value;
        }));

    public static Effect<int, Overdrawn, EmptyEnvironment> Transfer(Transaction tx, int amount)
    {
        const int BALANCE = 300;
        Console.WriteLine($"  transfer {amount} in transaction {tx.Id}");
        return amount <= BALANCE ? Effect<EmptyEnvironment>.Succeed(BALANCE - amount) : Effect<EmptyEnvironment>.Fail(new Overdrawn(amount - BALANCE));
    }
}

public static class Remote
{
    private static int calls;

    // Fails its first call, so the retry has something to do.
    public static Effect<string, TimeoutError, DefaultEnvironment> Fetch(string key, TimeSpan latency) => Scope<TimeoutError, DefaultEnvironment>.Gen(async scope =>
    {
        await scope.Run(Effect.Sleep<DefaultEnvironment>(latency));
        if (Interlocked.Increment(ref calls) == 1) throw scope.Fail(new TimeoutError(latency));
        return $"{key}:{latency.TotalMilliseconds}ms";
    });
}
