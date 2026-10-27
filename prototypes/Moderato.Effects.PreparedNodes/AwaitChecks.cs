static class AwaitChecks
{
    private const int WARMUP = 2048;
    private const int ITERATIONS = 4096;

    public static void Run()
    {
        // Given
        using var loop = new NotificationLoop(2);
        var first = new PreparedCompletion<int, string>(loop);
        var second = new PreparedCompletion<int, string>(loop);
        using var requested = new AutoResetEvent(false);
        using var finished = new AutoResetEvent(false);
        var ambient = new AsyncLocal<string?> { Value = "consumer" };
        long token = 0;
        var consumer = Consume();
        long before = 0;
        long workerBefore = 0;

        // When
        for (var i = 0; i < WARMUP + ITERATIONS; i++)
        {
            Check(requested.WaitOne(TimeSpan.FromSeconds(5)), "Await request timed out.");
            if (i == WARMUP)
            {
                workerBefore = WaitIdle(loop);
                before = GC.GetAllocatedBytesForCurrentThread();
            }
            var outcome = (i & 1) == 0 ? Result<int, string>.Ok(42) : Result<int, string>.Err("expected");
            Check(first.TryComplete(Volatile.Read(ref token), outcome) == Status.Ok, "Completion rejected.");
        }
        Check(finished.WaitOne(TimeSpan.FromSeconds(5)), "Await loop timed out.");
        var producerBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        consumer.GetAwaiter().GetResult();
        var workerBytes = WaitIdle(loop) - workerBefore;

        // Then
        Check(producerBytes == 0, "Producer allocated after preparation.");
        Check(workerBytes == 0, "Await dispatch or consumer loop allocated after preparation.");
        using var resumed = new AutoResetEvent(false);
        PreparedAwaitable<int, string>.Awaiter earlyAwaiter = default;
        Result<int, string> earlyResult = default;
        Action onEarly = () => { earlyResult = earlyAwaiter.GetResult(); resumed.Set(); };
        for (var i = 0; i < WARMUP; i++) ExerciseEarly();
        var raceWorkerBefore = WaitIdle(loop);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ITERATIONS; i++) ExerciseEarly();
        var raceProducerBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var raceWorkerBytes = WaitIdle(loop) - raceWorkerBefore;
        Check(raceProducerBytes == 0 && raceWorkerBytes == 0, "Registration-after-completion allocated.");

        Check(first.TryStart(out var contextual) == Status.Ok, "Context setup failed.");
        var contextualAwaiter = contextual.GetAwaiter();
        string? seenContext = null;
        ambient.Value = "registration";
        contextualAwaiter.OnCompleted(() =>
        {
            seenContext = ambient.Value;
            _ = contextualAwaiter.GetResult();
            resumed.Set();
        });
        ambient.Value = "producer";
        Check(first.TryComplete(contextual.Token, Result<int, string>.Ok(7)) == Status.Ok, "Context completion failed.");
        Check(resumed.WaitOne(TimeSpan.FromSeconds(5)) && seenContext == "registration", "Explicit OnCompleted context lost.");
        ambient.Value = "consumer";
        Check(first.TryStart(out var left) == Status.Ok, "First concurrent slot setup failed.");
        Check(second.TryStart(out var right) == Status.Ok, "Second concurrent slot setup failed.");
        var leftTask = Read(left);
        var rightTask = Read(right);
        Check(first.TryStart(out _) == Status.Busy, "Active slot overwritten.");
        Check(second.TryComplete(right.Token, Result<int, string>.Ok(2)) == Status.Ok, "Second completion failed.");
        Check(first.TryComplete(left.Token, Result<int, string>.Ok(1)) == Status.Ok, "First completion failed.");
        Check(Task.WaitAll([leftTask, rightTask], TimeSpan.FromSeconds(5)), "Concurrent awaits timed out.");
        Check(leftTask.Result == 1 && rightTask.Result == 2, "Concurrent results mixed.");
        Check(first.TryComplete(left.Token, Result<int, string>.Ok(9)) == Status.Invalid, "Old token completed again.");
        Check(left.GetAwaiter().GetResult().Status == Status.Invalid, "Result consumed twice.");
        Check(loop.Failure is null, "Notification callback failed.");
        Console.WriteLine($"Prepared await producer={producerBytes} bytes; worker dispatch including consumer={workerBytes} bytes.");
        Console.WriteLine($"Prepared completion-before-registration producer={raceProducerBytes} bytes; worker={raceWorkerBytes} bytes.");
        Console.WriteLine("Actual await loop, expected errors, ExecutionContext, registration-after-completion and two independent slots verified.");

        void ExerciseEarly()
        {
            Check(first.TryStart(out var early) == Status.Ok, "Early setup failed.");
            earlyAwaiter = early.GetAwaiter();
            Check(!earlyAwaiter.IsCompleted, "Early source already completed.");
            Check(first.TryComplete(early.Token, Result<int, string>.Ok(7)) == Status.Ok, "Early completion failed.");
            earlyAwaiter.UnsafeOnCompleted(onEarly);
            Check(resumed.WaitOne(TimeSpan.FromSeconds(5)), "Registration-after-completion lost notification.");
            Check(earlyResult.Value == 7, "Early result lost.");
        }

        async Task Consume()
        {
            try
            {
                for (var i = 0; i < WARMUP + ITERATIONS; i++)
                {
                    Check(first.TryStart(out var pending) == Status.Ok, "Consumer start failed.");
                    Volatile.Write(ref token, pending.Token);
                    requested.Set();
                    var outcome = await pending;
                    Check(ambient.Value == "consumer", "ExecutionContext lost.");
                    Check((i & 1) == 0 ? outcome.Status == Status.Ok && outcome.Value == 42 : outcome.Status == Status.Error && outcome.Error == "expected", "Await result lost.");
                }
            }
            finally { finished.Set(); }
        }
    }

    private static long WaitIdle(NotificationLoop loop)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            if (loop.TryReadIdleAllocation(out var bytes)) return bytes;
            if (Environment.TickCount64 > deadline) throw new Exception("Notification worker did not become idle.");
            Thread.Yield();
        }
    }

    private static async Task<int> Read(PreparedAwaitable<int, string> value) => (await value).Value;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
