using Moderato.Effects;
using Moderato.Results;

var env = new AppEnv(new InMemoryUsers(), new Permissions(), TimeProvider.System, new ConsoleAudit());

// 1. Gen blocks: each step's error widens to AppError, and an Err stops the block.
foreach (var id in new[] { 1, 2, 3 })
{
    // The token is explicit to avoid a spurious CS8655 from the .NET 11 RC2 compiler.
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
