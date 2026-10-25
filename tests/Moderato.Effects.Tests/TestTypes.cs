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

public sealed class TimeEnvironment(TimeProvider time) : IHas<TimeProvider>
{
    TimeProvider IHas<TimeProvider>.Service => time;
}

public partial union RequestError(NotFound, TimeoutError);

public partial union RequestError : IUnionCase<RequestError, NotFound>, IUnionCase<RequestError, TimeoutError>
{
    static RequestError IUnionCase<RequestError, NotFound>.From(NotFound value) => value;

    static RequestError IUnionCase<RequestError, TimeoutError>.From(TimeoutError value) => value;

    static bool IUnionCase<RequestError, NotFound>.TryGet(RequestError self, out NotFound value)
    {
        if (self is NotFound found)
        {
            value = found;
            return true;
        }

        value = default!;
        return false;
    }

    static bool IUnionCase<RequestError, TimeoutError>.TryGet(RequestError self, out TimeoutError value)
    {
        if (self is TimeoutError timeout)
        {
            value = timeout;
            return true;
        }

        value = default!;
        return false;
    }
}

/// <summary>A clock that records each wait and lets it pass at once, so schedules can be checked without waiting.</summary>
public sealed class RecordingClock : TimeProvider
{
    private readonly List<TimeSpan> _waits = [];
    private long _now;

    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (_waits) return [.. _waits];
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _now);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_waits) _waits.Add(dueTime);
            Interlocked.Add(ref _now, dueTime.Ticks);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new IdleTimer();
    }

    private sealed class IdleTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class RetryEnvironment(TimeProvider time, Random random) : IHas<TimeProvider>, IHas<Random>
{
    TimeProvider IHas<TimeProvider>.Service => time;

    Random IHas<Random>.Service => random;
}
