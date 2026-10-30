using System.Runtime.CompilerServices;

sealed class PreparedCompletion<T, E> : INotification
{
    private readonly NotificationLoop loop;
    private SpinLock gate = new(false);
    private long epoch;
    private bool active;
    private bool completed;
    private bool queued;
    private bool registered;
    private Action? continuation;
    private ExecutionContext? context;
    private Result<T, E> result;

    public PreparedCompletion(NotificationLoop loop)
    {
        this.loop = loop;
        loop.Register();
    }

    public Status TryStart(out PreparedAwaitable<T, E> awaitable)
    {
        awaitable = default;
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            if (active || queued) return Status.Busy;
            if (epoch == long.MaxValue) return Status.EpochExhausted;
            active = true;
            completed = false;
            registered = false;
            result = default;
            awaitable = new(this, ++epoch);
            return Status.Ok;
        }
        finally { if (taken) gate.Exit(); }
    }

    public Status TryComplete(long token, Result<T, E> value)
    {
        var schedule = false;
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            if (!active || epoch != token || completed) return Status.Invalid;
            result = value;
            completed = true;
            if (registered) { queued = true; schedule = true; }
        }
        finally { if (taken) gate.Exit(); }
        if (schedule) loop.Enqueue(this);
        return Status.Ok;
    }

    public bool IsCompleted(long token)
    {
        var taken = false;
        try { gate.Enter(ref taken); return !active || token != epoch || completed; }
        finally { if (taken) gate.Exit(); }
    }

    public Result<T, E> Take(long token)
    {
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            if (!active || token != epoch || queued) return Result<T, E>.Stop(Status.Invalid);
            if (!completed) return Result<T, E>.Stop(Status.Pending);
            var value = result;
            result = default;
            active = false;
            return value;
        }
        finally { if (taken) gate.Exit(); }
    }

    public void Register(long token, Action callback, bool flowContext)
    {
        var captured = flowContext ? ExecutionContext.Capture() : null;
        var schedule = false;
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            if (!active || token != epoch || registered) throw new InvalidOperationException("Invalid continuation registration.");
            continuation = callback;
            context = captured;
            registered = true;
            if (completed) { queued = true; schedule = true; }
        }
        finally { if (taken) gate.Exit(); }
        if (schedule) loop.Enqueue(this);
    }

    public void Dispatch()
    {
        Action callback;
        ExecutionContext? captured;
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            callback = continuation!;
            captured = context;
            continuation = null;
            context = null;
            queued = false;
        }
        finally { if (taken) gate.Exit(); }
        // GetResult can release the slot and start another run inside the callback.
        if (captured is null) callback();
        else ExecutionContext.Run(captured, static state => ((Action)state!).Invoke(), callback);
    }
}

readonly record struct PreparedAwaitable<T, E>(PreparedCompletion<T, E>? Source, long Token)
{
    public Awaiter GetAwaiter() => new(Source, Token);

    public readonly record struct Awaiter(PreparedCompletion<T, E>? Source, long Token) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => Source is null || Source.IsCompleted(Token);
        public Result<T, E> GetResult() => Source is null ? Result<T, E>.Stop(Status.Invalid) : Source.Take(Token);
        public void OnCompleted(Action continuation) => Source!.Register(Token, continuation, true);
        public void UnsafeOnCompleted(Action continuation) => Source!.Register(Token, continuation, false);
    }
}
