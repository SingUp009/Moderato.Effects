namespace Moderato.Effects.Tests;

public record User(int Id, string Name);
public record NotFound(int Id);
public record Forbidden(string Reason);
[Error]
public partial union AppError(NotFound, Forbidden);

public sealed class Counter
{
    private int value;

    public int Next() => ++value;
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

[Error]
public partial union RequestError(NotFound, TimeoutError);

/// <summary>A clock that records each wait and lets it pass at once, so schedules can be checked without waiting.</summary>
public sealed class RecordingClock : TimeProvider
{
    private readonly List<TimeSpan> waits = [];
    private long now;

    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (waits) return [.. waits];
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref now);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (waits) waits.Add(dueTime);
            Interlocked.Add(ref now, dueTime.Ticks);
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

public record ConnFailed(string Host);

public record QueryFailed(string Sql);

public union DbError(ConnFailed, QueryFailed);

[Error]
public partial union StoreError(DbError, NotFound);

[Error, RestOf<AppError>]
public partial union Denied(Forbidden);
