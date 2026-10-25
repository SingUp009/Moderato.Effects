using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

// Running eight small async steps together: Task.WhenAll against Effect.All in both modes.
[MemoryDiagnoser]
public class AllBenchmarks
{
    private const int Count = 8;

    private Effect<int[], string, EmptyEnvironment> _sequential;
    private Effect<int[], string, EmptyEnvironment> _parallel;

    [GlobalSetup]
    public void Setup()
    {
        var steps = Enumerable.Range(0, Count).Select(static i => Effect<EmptyEnvironment>.Async(async _ =>
        {
            await Task.Yield();
            return i;
        })).Select(static e => (Effect<int, string, EmptyEnvironment>)e).ToArray();
        _sequential = Effect.All(steps);
        _parallel = Effect.All(steps, Concurrency.Parallel);
    }

    private static async Task<int> Step(int i)
    {
        await Task.Yield();
        return i;
    }

    [Benchmark(Baseline = true, Description = "Task.WhenAll")]
    public async Task<int> WhenAll() => (await Task.WhenAll(Enumerable.Range(0, Count).Select(Step)).ConfigureAwait(false)).Sum();

    [Benchmark(Description = "Effect.All (Sequential)")]
    public async Task<int> All_Sequential() => (await _sequential.RunAsync().ConfigureAwait(false)).Map(static v => v.Sum()).UnwrapOr(-1);

    [Benchmark(Description = "Effect.All (Parallel)")]
    public async Task<int> All_Parallel() => (await _parallel.RunAsync().ConfigureAwait(false)).Map(static v => v.Sum()).UnwrapOr(-1);
}
