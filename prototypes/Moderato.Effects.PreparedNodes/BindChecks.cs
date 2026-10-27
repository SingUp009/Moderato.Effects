static class BindChecks
{
    public static void Run()
    {
        // Given
        var graph = new Arena(2);
        var constants = new ConstantPool<int, string, int>(graph, 2);
        var binds = new BindPool<int, int, string, int>(graph, 6);
        var scratch = new Arena(1);
        var nextConstants = new ConstantPool<int, string, int>(scratch, 1);
        Check(constants.TryAdd(Result<int, string>.Ok(21), out var source) == Status.Ok, "Source setup failed.");
        Check(constants.TryAdd(Result<int, string>.Err("earlier"), out var failedSource) == Status.Ok, "Error setup failed.");
        var calls = 0;
        Computation<int, string, int> latest = default;
        Func<int, BuildResult<int, string, int>> next = value =>
        {
            calls++;
            var status = nextConstants.TryAdd(Result<int, string>.Ok(value * 2), out latest);
            return new(status, latest);
        };
        Check(binds.TryAdd(source, next, out var bound) == Status.Ok, "Bind setup failed.");
        Check(calls == 0, "Bind ran during construction.");
        for (var i = 0; i < 2048; i++) Exercise();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++) Exercise();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Then
        Check(allocated == 0, "Dynamic continuation allocated after preparation.");
        Check(calls == (2048 + 4096) * 2, "Bind was not called once per run.");
        var previousCalls = calls;
        Check(binds.TryAdd(failedSource, next, out var shortCircuit) == Status.Ok, "Error bind setup failed.");
        Check(shortCircuit.Run(0).Error == "earlier" && calls == previousCalls, "Source Err did not short circuit.");
        Check(scratch.TryReset() == Status.Ok, "Scratch reset failed.");
        Check(binds.TryAdd(source, _ =>
        {
            var status = nextConstants.TryAdd(Result<int, string>.Err("next"), out var failed);
            return new(status, failed);
        }, out var failedNext) == Status.Ok, "Next error setup failed.");
        Check(failedNext.Run(0).Error == "next", "Next Err lost.");
        Check(scratch.TryReset() == Status.Ok, "Scratch reset failed.");
        Check(nextConstants.TryAdd(Result<int, string>.Ok(9), out var old) == Status.Ok, "Old handle setup failed.");
        Check(scratch.TryReset() == Status.Ok, "Scratch reset failed.");
        Check(binds.TryAdd(source, _ => new(Status.Ok, old), out var staleNext) == Status.Ok, "Stale bind setup failed.");
        Check(staleNext.Run(0).Status == Status.Invalid, "Stale continuation executed.");
        Check(binds.TryAdd(source, value =>
        {
            var status = constants.TryAdd(Result<int, string>.Ok(value), out var sameArena);
            return new(status, sameArena);
        }, out var sameOwner) == Status.Ok, "Same-owner setup failed.");
        Check(sameOwner.Run(0).Status == Status.Busy, "Same-owner mutation was allowed during execution.");

        // Given
        Check(scratch.TryReset() == Status.Ok, "Scratch reset failed.");
        var input = new TaskCompletionSource<Result<int, string>>();
        var execution = new PendingExecution<int, int, int, string, int>(
            (value, _) => { Check(value == 42, "Dynamic source value lost."); return new(input.Task); }, static value => value + 1);

        // When
        Check(execution.TryStart(bound, 0, out var token) == Status.Ok, "Pending bind start failed.");
        Check(execution.Advance(token) == Status.Pending, "Bound graph did not suspend.");
        Check(graph.TryReset() == Status.Busy, "Pending graph reset.");
        Check(scratch.TryReset() == Status.Ok, "Consumed synchronous scratch stayed leased.");
        input.SetResult(Result<int, string>.Ok(42));
        Check(execution.Advance(token) == Status.Ok, "Pending continuation did not complete.");

        // Then
        Check(execution.TryTake(token, out var result) == Status.Ok && result.Value == 43, "Pending continuation result lost.");
        Check(graph.TryReset() == Status.Ok, "Graph lease leaked.");
        Check(bound.Run(0).Status == Status.Invalid, "Reset graph remained valid.");
        Console.WriteLine($"Prepared dynamic bind and capacity rejection with separate scratch: {allocated} bytes.");
        Console.WriteLine("Lazy construction, per-run continuation, source/next Err, stale next handle and bind-before-pending lifetime verified.");

        void Exercise()
        {
            // When
            Check(scratch.TryReset() == Status.Ok, "Scratch reset failed.");
            var result = bound.Run(0);
            var retained = latest;
            var exhausted = bound.Run(0);

            // Then
            Check(result.Status == Status.Ok && result.Value == 42, "Dynamic bind result lost.");
            Check(exhausted.Status == Status.Capacity, "Exhausted scratch grew or hid capacity failure.");
            Check(retained.Run(0).Value == 42, "Exhausted construction damaged existing node.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
