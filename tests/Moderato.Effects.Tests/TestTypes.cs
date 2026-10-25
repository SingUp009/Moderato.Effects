namespace Moderato.Effects.Tests;

public record User(int Id, string Name);
public record NotFound(int Id);
public record Forbidden(string Reason);
public union AppError(NotFound, Forbidden);

public sealed class Counter
{
    private int _value;

    public int Next() => ++_value;
}

public sealed class CounterEnvironment(Counter counter) : IHas<Counter>
{
    Counter IHas<Counter>.Service => counter;
}
