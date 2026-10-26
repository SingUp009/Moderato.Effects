using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

// Eight small async steps: Task.WhenAll vs Effect.All in both modes.
[MemoryDiagnoser]
public class AllBenchmarks
{
    private const int COUNT = 8;

    private Effect<int[], string, EmptyEnvironment> sequential;
    private Effect<int[], string, EmptyEnvironment> parallel;

    [GlobalSetup]
    public void Setup()
    {
        var steps = Enumerable.Range(0, COUNT).Select(static i => Effect<EmptyEnvironment>.Async(async _ =>
        {
            await Task.Yield();
            return i;
        })).Select(static e => (Effect<int, string, EmptyEnvironment>)e).ToArray();
        sequential = Effect.All(steps);
        parallel = Effect.All(steps, Concurrency.Parallel);
    }

    private static async Task<int> Step(int i)
    {
        await Task.Yield();
        return i;
    }

    [Benchmark(Baseline = true, Description = "Task.WhenAll")]
    public async Task<int> WhenAll() => (await Task.WhenAll(Enumerable.Range(0, COUNT).Select(Step)).ConfigureAwait(false)).Sum();

    [Benchmark(Description = "Effect.All (Sequential)")]
    public async Task<int> All_Sequential() => (await sequential.RunAsync().ConfigureAwait(false)).Map(static v => v.Sum()).UnwrapOr(-1);

    [Benchmark(Description = "Effect.All (Parallel)")]
    public async Task<int> All_Parallel() => (await parallel.RunAsync().ConfigureAwait(false)).Map(static v => v.Sum()).UnwrapOr(-1);
}
