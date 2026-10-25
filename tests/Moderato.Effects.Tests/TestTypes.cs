namespace Moderato.Effects.Tests;

public record User(int Id, string Name);
public record NotFound(int Id);
public record Forbidden(string Reason);
public partial union AppError(NotFound, Forbidden);

// Hand-written until the source generator (P6) emits it for [Error] unions; it is also the reference for that output.
public partial union AppError : IUnionCase<AppError, NotFound>, IUnionCase<AppError, Forbidden>
{
    static AppError IUnionCase<AppError, NotFound>.From(NotFound value) => value;

    static AppError IUnionCase<AppError, Forbidden>.From(Forbidden value) => value;

    static bool IUnionCase<AppError, NotFound>.TryGet(AppError self, out NotFound value)
    {
        if (self is NotFound found)
        {
            value = found;
            return true;
        }

        value = default!;
        return false;
    }

    static bool IUnionCase<AppError, Forbidden>.TryGet(AppError self, out Forbidden value)
    {
        if (self is Forbidden forbidden)
        {
            value = forbidden;
            return true;
        }

        value = default!;
        return false;
    }
}

public sealed class Counter
{
    private int _value;

    public int Next() => ++_value;
}

public sealed class CounterEnvironment(Counter counter) : IHas<Counter>
{
    Counter IHas<Counter>.Service => counter;
}

public sealed class Clock(DateTimeOffset now) : IHas<TimeProvider>
{
    TimeProvider IHas<TimeProvider>.Service => new FixedTimeProvider(now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
