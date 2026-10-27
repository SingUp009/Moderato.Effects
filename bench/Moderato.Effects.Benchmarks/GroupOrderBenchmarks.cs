using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class GroupOrderBenchmarks
{
    [Params(8, 256, 257, 1024)]
    public int Count;

    private Effect<int[], string[], EmptyEnvironment> effect;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, EmptyEnvironment>[] children = [.. Enumerable.Range(0, Count)
            .Select(i => (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail(i.ToString()))];
        var failure = new IOException("last child");
        children[^1] = Effect<EmptyEnvironment>.Sync<int>(() => throw failure);
        effect = Effect.AllErrors(children);
        if (!Run().Result.TryGetValue(out Die? die) || !ReferenceEquals(die.Exception, failure)
            || die.Suppressed.Count != Math.Min(100, Count - 1) || die.OmittedSuppressed != Math.Max(0, Count - 101))
        {
            throw new InvalidOperationException("Unexpected group failure context.");
        }
    }

    [Benchmark] public ValueTask<Exit<int[], string[]>> Run() => effect.RunExitAsync();
}
