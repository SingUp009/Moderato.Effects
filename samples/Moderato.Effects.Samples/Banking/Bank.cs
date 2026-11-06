using Moderato.Effects;
using Moderato.Results;

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
