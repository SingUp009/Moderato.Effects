using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class GroupWaitBenchmarks
{
    [Params(1, 16)]
    public int Count;

    [Params(false, true)]
    public bool Wait;

    private Effect<int[], string, EmptyEnvironment> group;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, EmptyEnvironment>[] children = new Effect<int, string, EmptyEnvironment>[Count];
        for (var i = 0; i < children.Length; i++)
        {
            var value = i;
            children[i] = Effect<EmptyEnvironment>.Async(async token =>
            {
                if (Wait) await Task.Delay(1, token).ConfigureAwait(false);
                return value;
            });
        }
        group = Effect.All(children, Concurrency.Parallel.WithMaxDegree(2));
        var result = Run().AsTask().GetAwaiter().GetResult();
        if (!result.TryGetOk(out var values) || !values.SequenceEqual(Enumerable.Range(0, Count)))
        {
            throw new InvalidOperationException("Unexpected benchmark result.");
        }
    }

    [Benchmark] public ValueTask<Result<int[], string>> Run() => group.RunAsync(CancellationToken.None);
}
