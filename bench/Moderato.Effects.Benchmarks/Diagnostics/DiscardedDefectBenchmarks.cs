using BenchmarkDotNet.Attributes;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class DiscardedDefectBenchmarks
{
    [Params(8, 128)] public int Count { get; set; }
    private Effect<int[], string, Gate> effect;
    private readonly IOException failure = new("child failure");

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, Gate> child = Effect<Gate>.AccessAsync<int>(async (gate, _) =>
        {
            if (Interlocked.Increment(ref gate.Started) == Count) gate.Ready.SetResult();
            await gate.Ready.Task.ConfigureAwait(false);
            throw failure;
        });
        effect = Effect.All(Enumerable.Repeat(child, Count), Concurrency.Parallel);
        var exit = Run().AsTask().GetAwaiter().GetResult();
        if (!exit.TryGetValue(out Die? die) || !ReferenceEquals(die.Exception, failure)
            || die.Suppressed.Count != Math.Min(100, Count - 1)
            || die.OmittedSuppressed != Math.Max(0, Count - 101))
        {
            throw new InvalidOperationException("Unexpected defect context.");
        }
    }

    [Benchmark] public ValueTask<Exit<int[], string>> Run() => effect.RunExitAsync(new Gate(), CancellationToken.None);

    private sealed class Gate
    {
        public int Started;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
