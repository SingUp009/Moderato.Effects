using System.Threading.Tasks.Sources;

static class PendingChecks
{
    public static void Run()
    {
        // Given
        var arena = new Arena(1);
        var constants = new ConstantPool<int, string, int>(arena, 2);
        Check(constants.TryAdd(Result<int, string>.Ok(20), out var computation) == Status.Ok, "Source setup failed.");
        Check(constants.TryAdd(Result<int, string>.Err("earlier"), out var failedSource) == Status.Ok, "Error source setup failed.");
        var input = new ManualInput();
        var bindCalls = 0;
        var mapCalls = 0;
        var execution = new PendingExecution<int, int, int, string, int>(
            (value, env) => { bindCalls++; Check(value + env == 21, "Environment lost."); return input.Task; },
            value => { mapCalls++; return value + 1; });
        for (var i = 0; i < 2048; i++) Exercise(false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++) Exercise(false);
        var pendingBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        for (var i = 0; i < 2048; i++) Exercise(true);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++) Exercise(true);
        var racingBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Then
        Check(pendingBytes == 0 && racingBytes == 0, "Prepared progress allocated.");
        Check(bindCalls == mapCalls, "Bind or map repeated.");
        for (var i = 0; i < 2048; i++) { ExerciseError(false); ExerciseError(true); }
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++) { ExerciseError(false); ExerciseError(true); }
        var errorBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(errorBytes == 0, "Typed errors allocated.");

        // Given
        input.Reset(false);
        Check(execution.TryStart(computation, 1, out var token) == Status.Ok, "Start failed.");
        Check(execution.Advance(token) == Status.Pending, "Input did not suspend.");
        using var completed = new ManualResetEventSlim();
        var worker = new Thread(() => { input.Complete(Result<int, string>.Ok(41)); completed.Set(); });

        // When
        worker.Start();
        Check(completed.Wait(TimeSpan.FromSeconds(5)), "Worker timed out.");
        worker.Join();
        Check(execution.Advance(token) == Status.Ok, "Cross-thread input did not complete.");

        // Then
        Check(execution.TryTake(token, out var crossThread) == Status.Ok && crossThread.Value == 42, "Cross-thread result lost.");
        Check(execution.TryTake(token, out _) == Status.Invalid, "Result consumed twice.");
        input.Reset(false);
        Check(execution.TryStart(computation, 1, out var newerToken) == Status.Ok, "Reuse failed.");
        Check(execution.Advance(token) == Status.Invalid, "Old token advanced new execution.");
        Check(execution.TryTake(token, out _) == Status.Invalid, "Old token consumed new result.");
        input.Complete(Result<int, string>.Err("expected"));
        var priorMaps = mapCalls;
        Check(execution.Advance(newerToken) == Status.Ok, "Err did not complete.");
        Check(execution.TryTake(newerToken, out var error) == Status.Ok && error.Status == Status.Error && error.Error == "expected", "Err lost.");
        Check(mapCalls == priorMaps, "Map ran after Err.");
        var priorBinds = bindCalls;
        Check(execution.TryStart(failedSource, 1, out token) == Status.Ok, "Error source start failed.");
        Check(execution.Advance(token) == Status.Ok, "Error source did not complete.");
        Check(execution.TryTake(token, out error) == Status.Ok && error.Error == "earlier", "Source Err lost.");
        Check(bindCalls == priorBinds && mapCalls == priorMaps, "Continuation ran after source Err.");
        input.Reset(false);
        Check(execution.TryStart(computation, 1, out token) == Status.Ok, "Defect run failed.");
        Check(execution.Advance(token) == Status.Pending, "Defect input did not suspend.");
        var failure = new IOException("input failed");
        input.Fail(failure);
        Check(execution.Advance(token) == Status.Ok, "Defect did not complete.");
        Check(execution.TryTake(token, out var defect) == Status.Ok && defect.Status == Status.Faulted && ReferenceEquals(defect.Exception, failure), "Defect reference lost.");
        Check(arena.TryReset() == Status.Ok, "Result consumption did not release arena.");
        Check(execution.TryStart(computation, 1, out _) == Status.Invalid, "Stale graph started.");
        Check(arena.TryReset() == Status.Ok, "Rejected start leaked lease.");
        Console.WriteLine($"Prepared explicit progress: pending={pendingBytes} B; completion-during-status={racingBytes} B; typed-errors={errorBytes} B.");
        Console.WriteLine("Pending/unconsumed ownership, cross-thread completion, single consumption, stale tokens, Err and input defect verified.");

        void ExerciseError(bool failBeforeBind)
        {
            input.Reset(false);
            var previousBinds = bindCalls;
            var previousMaps = mapCalls;
            Check(execution.TryStart(failBeforeBind ? failedSource : computation, 1, out var run) == Status.Ok, "Err start failed.");
            if (!failBeforeBind)
            {
                Check(execution.Advance(run) == Status.Pending, "Err input did not suspend.");
                input.Complete(Result<int, string>.Err("expected"));
            }
            Check(execution.Advance(run) == Status.Ok, "Err completion failed.");
            Check(execution.TryTake(run, out var value) == Status.Ok && value.Status == Status.Error, "Err lost.");
            Check(value.Error == (failBeforeBind ? "earlier" : "expected"), "Err value changed.");
            Check(mapCalls == previousMaps && bindCalls == previousBinds + (failBeforeBind ? 0 : 1), "Err continuation repeated.");
        }

        void Exercise(bool completeDuringStatus)
        {
            // When
            input.Reset(completeDuringStatus);
            Check(execution.TryStart(computation, 1, out var run) == Status.Ok, "Start failed.");
            Check(execution.TryStart(computation, 1, out _) == Status.Busy, "Execution overwritten.");
            Check(arena.TryReset() == Status.Busy, "Pending graph reset.");
            Check(execution.TryTake(run, out _) == Status.Pending, "Incomplete result consumed.");
            Check(execution.Advance(run) == Status.Pending, "Input did not suspend.");
            if (!completeDuringStatus) input.Complete(Result<int, string>.Ok(41));
            Check(execution.Advance(run) == Status.Ok, "Completion failed.");
            Check(execution.Advance(run) == Status.Ok, "Completed execution did not stay complete.");
            Check(arena.TryReset() == Status.Busy, "Unconsumed graph reset.");
            Check(execution.TryStart(computation, 1, out _) == Status.Busy, "Unconsumed result overwritten.");

            // Then
            Check(execution.TryTake(run, out var value) == Status.Ok && value.Value == 42, "Wrong mapped value.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class ManualInput : IValueTaskSource<Result<int, string>>
    {
        private ManualResetValueTaskSourceCore<Result<int, string>> core;
        private bool completeDuringStatus;
        public ValueTask<Result<int, string>> Task => new(this, core.Version);
        public void Reset(bool duringStatus) { core.Reset(); completeDuringStatus = duringStatus; }
        public void Complete(Result<int, string> value) => core.SetResult(value);
        public void Fail(Exception exception) => core.SetException(exception);
        public Result<int, string> GetResult(short token) => core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token)
        {
            var status = core.GetStatus(token);
            if (completeDuringStatus && status == ValueTaskSourceStatus.Pending)
            {
                completeDuringStatus = false;
                core.SetResult(Result<int, string>.Ok(41));
            }
            return status;
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => throw new InvalidOperationException("Explicit progress must not register a continuation.");
    }
}
