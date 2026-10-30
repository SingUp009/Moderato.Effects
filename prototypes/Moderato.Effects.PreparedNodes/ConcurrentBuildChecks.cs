static class ConcurrentBuildChecks
{
    public static void Run()
    {
        // Given
        var graph = new Arena(2);
        var constants = new ConstantPool<int, string, int>(graph, 1);
        var binds = new BindPool<int, int, string, int>(graph, 1);
        Check(constants.TryAdd(Result<int, string>.Ok(21), out var source) == Status.Ok, "Shared source setup failed.");
        Check(binds.TryAdd(source, static (value, context) =>
        {
            var storage = context?.Find<Scratch>();
            return storage is null ? new(Status.Invalid, default) : storage.Build(value);
        }, out var shared) == Status.Ok, "Shared bind setup failed.");
        using var overlap = new Barrier(2);
        var leftScratch = new Scratch(overlap);
        var rightScratch = new Scratch(overlap);
        var leftContext = new BuildContext(leftScratch);
        var rightContext = new BuildContext(rightScratch);
        var leftInput = new TaskCompletionSource<Result<int, string>>();
        var rightInput = new TaskCompletionSource<Result<int, string>>();
        var left = new PendingExecution<int, int, int, string, int>((_, _) => new(leftInput.Task), static value => value);
        var right = new PendingExecution<int, int, int, string, int>((_, _) => new(rightInput.Task), static value => value);
        Check(left.TryStart(shared, 0, out var leftToken, leftContext) == Status.Ok, "Left start failed.");
        Check(right.TryStart(shared, 0, out var rightToken, rightContext) == Status.Ok, "Right start failed.");
        Status leftState = Status.Invalid, rightState = Status.Invalid;
        var leftWorker = new Thread(() => leftState = left.Advance(leftToken));
        var rightWorker = new Thread(() => rightState = right.Advance(rightToken));

        // When
        leftWorker.Start();
        rightWorker.Start();
        Check(leftWorker.Join(TimeSpan.FromSeconds(10)), "Left build timed out.");
        Check(rightWorker.Join(TimeSpan.FromSeconds(10)), "Right build timed out.");

        // Then
        Check(leftState == Status.Pending && rightState == Status.Pending, "Overlapping bind failed.");
        Check(leftScratch.Last.Run(0).Value == 42 && rightScratch.Last.Run(0).Value == 42, "Independent scratch results lost.");
        Check(graph.TryReset() == Status.Busy, "Shared graph reset while both runs pending.");
        Check(leftScratch.Arena.TryReset() == Status.Ok, "Left scratch reset failed.");
        Check(leftScratch.Last.Run(0).Status == Status.Invalid, "Left handle survived reset.");
        Check(rightScratch.Last.Run(0).Value == 42, "Left reset invalidated right scratch.");
        leftInput.SetResult(Result<int, string>.Ok(1));
        Check(left.Advance(leftToken) == Status.Ok, "Left completion failed.");
        Check(left.TryTake(leftToken, out var leftResult) == Status.Ok && leftResult.Value == 1, "Left result lost.");
        Check(graph.TryReset() == Status.Busy, "Finishing one run released both leases.");
        rightInput.SetResult(Result<int, string>.Ok(2));
        Check(right.Advance(rightToken) == Status.Ok, "Right completion failed.");
        Check(right.TryTake(rightToken, out var rightResult) == Status.Ok && rightResult.Value == 2, "Right result lost.");
        Check(graph.TryReset() == Status.Ok, "Shared graph lease leaked.");
        Console.WriteLine("One graph concurrently built in two execution-local scratch regions, with independent results and leases.");
    }

    private sealed class Scratch
    {
        private readonly Barrier overlap;
        private readonly ConstantPool<int, string, int> constants;
        public Arena Arena { get; } = new(1);
        public Computation<int, string, int> Last;

        public Scratch(Barrier overlap)
        {
            this.overlap = overlap;
            constants = new(Arena, 1);
        }

        public BuildResult<int, string, int> Build(int value)
        {
            if (!overlap.SignalAndWait(TimeSpan.FromSeconds(5))) return new(Status.Invalid, default);
            var status = constants.TryAdd(Result<int, string>.Ok(value * 2), out Last);
            return new(status, Last);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
