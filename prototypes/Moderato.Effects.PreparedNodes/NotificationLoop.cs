interface INotification { void Dispatch(); }

sealed class NotificationLoop : IDisposable
{
    private readonly INotification?[] queue;
    private readonly AutoResetEvent ready = new(false);
    private readonly Thread worker;
    private SpinLock gate = new(false);
    private int head;
    private int count;
    private int registered;
    private bool stopping;
    private bool running;
    private long allocated;
    public Exception? Failure;

    public NotificationLoop(int capacity)
    {
        queue = new INotification[capacity];
        worker = new Thread(Work) { IsBackground = true };
        worker.Start();
    }

    public void Register()
    {
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            if (stopping || registered == queue.Length) throw new InvalidOperationException("Notification preparation capacity exhausted.");
            registered++;
        }
        finally { if (taken) gate.Exit(); }
    }

    public void Enqueue(INotification item)
    {
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            if (stopping || count == queue.Length) throw new InvalidOperationException("Reserved notification capacity violated.");
            queue[(head + count) % queue.Length] = item;
            count++;
        }
        finally { if (taken) gate.Exit(); }
        ready.Set();
    }

    private void Work()
    {
        while (true)
        {
            INotification? item = null;
            var taken = false;
            var exit = false;
            try
            {
                gate.Enter(ref taken);
                if (count != 0)
                {
                    item = queue[head];
                    queue[head] = null;
                    head = (head + 1) % queue.Length;
                    count--;
                    running = true;
                }
                else
                {
                    exit = stopping;
                }
            }
            finally { if (taken) gate.Exit(); }
            if (exit) return;
            if (item is null) { ready.WaitOne(); continue; }
            try { item.Dispatch(); }
            catch (Exception exception) { Failure = exception; }
            taken = false;
            try
            {
                gate.Enter(ref taken);
                Interlocked.Exchange(ref allocated, GC.GetAllocatedBytesForCurrentThread());
                running = false;
            }
            finally { if (taken) gate.Exit(); }
        }
    }

    public bool TryReadIdleAllocation(out long bytes)
    {
        var taken = false;
        try
        {
            gate.Enter(ref taken);
            bytes = Interlocked.Read(ref allocated);
            return count == 0 && !running;
        }
        finally { if (taken) gate.Exit(); }
    }

    public void Dispose()
    {
        var taken = false;
        try { gate.Enter(ref taken); stopping = true; }
        finally { if (taken) gate.Exit(); }
        ready.Set();
        worker.Join();
        ready.Dispose();
    }
}
