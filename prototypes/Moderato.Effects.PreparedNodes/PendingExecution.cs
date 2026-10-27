sealed class PendingExecution<T, U, V, E, R>(Func<T, R, ValueTask<Result<U, E>>> bind, Func<U, V> map)
{
    private Computation<T, E, R> source;
    private R environment = default!;
    private ValueTask<Result<U, E>> pending;
    private Result<V, E> result;
    private long epoch;
    private int gate;
    private Phase phase;

    public Status TryStart(Computation<T, E, R> computation, R env, out long token)
    {
        token = 0;
        if (Interlocked.CompareExchange(ref gate, 1, 0) != 0) return Status.Busy;
        try
        {
            if (phase != Phase.Idle) return Status.Busy;
            if (epoch == long.MaxValue) return Status.EpochExhausted;
            if (computation.Node is null) return Status.Invalid;
            if (!computation.Node.Owner.TryRead()) return Status.Busy;
            if (!computation.BelongsTo(computation.Node.Owner))
            {
                computation.Node.Owner.EndRead();
                return Status.Invalid;
            }
            source = computation;
            environment = env;
            token = ++epoch;
            phase = Phase.Start;
            return Status.Ok;
        }
        finally { Volatile.Write(ref gate, 0); }
    }

    public Status Advance(long token)
    {
        if (Interlocked.CompareExchange(ref gate, 1, 0) != 0) return Status.Busy;
        try
        {
            if (token != epoch || phase == Phase.Idle) return Status.Invalid;
            if (phase == Phase.Done) return Status.Ok;
            try
            {
                if (phase == Phase.Start)
                {
                    var first = source.Read(environment);
                    if (first.Status != Status.Ok)
                    {
                        result = new(first.Status, default!, first.Error, first.Exception);
                        phase = Phase.Done;
                        return Status.Ok;
                    }
                    pending = bind(first.Value, environment);
                    phase = Phase.Pending;
                }
                if (!pending.IsCompleted) return Status.Pending;
                var completed = pending.GetAwaiter().GetResult();
                result = completed.Status == Status.Ok
                    ? Result<V, E>.Ok(map(completed.Value))
                    : new(completed.Status, default!, completed.Error, completed.Exception);
            }
            catch (Exception exception)
            {
                result = new(Status.Faulted, default!, default!, exception);
            }
            phase = Phase.Done;
            return Status.Ok;
        }
        finally { Volatile.Write(ref gate, 0); }
    }

    public Status TryTake(long token, out Result<V, E> outcome)
    {
        outcome = default;
        if (Interlocked.CompareExchange(ref gate, 1, 0) != 0) return Status.Busy;
        try
        {
            if (token != epoch || phase == Phase.Idle) return Status.Invalid;
            if (phase != Phase.Done) return Status.Pending;
            outcome = result;
            var owner = source.Node!.Owner;
            source = default;
            environment = default!;
            pending = default;
            result = default;
            phase = Phase.Idle;
            owner.EndRead();
            return Status.Ok;
        }
        finally { Volatile.Write(ref gate, 0); }
    }

    private enum Phase { Idle, Start, Pending, Done }
}
