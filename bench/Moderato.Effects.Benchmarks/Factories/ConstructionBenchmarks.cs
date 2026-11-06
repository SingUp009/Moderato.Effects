using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

// Measured apart from runs, since a run that allocates nothing says nothing about construction.
[MemoryDiagnoser]
public class ConstructionBenchmarks
{
    private Effect<int, string, EmptyEnvironment> source;

    [GlobalSetup]
    public void Setup() => source = Effect<EmptyEnvironment>.Succeed(20);

    [Benchmark] public Effect<int, Never, EmptyEnvironment> Succeed() => Effect<EmptyEnvironment>.Succeed(20);
    [Benchmark] public Effect<int, string, EmptyEnvironment> Map() => source.Map(static x => x + 2);
    [Benchmark] public Effect<int, string, EmptyEnvironment> AndThen() => source.AndThen(static x => Effect<EmptyEnvironment>.Succeed(x / 2));
    [Benchmark] public ValueTask<Result<int, string>> BuildAndRun() => source.Map(static x => x + 2).AndThen(static x => Effect<EmptyEnvironment>.Succeed(x / 2)).RunAsync();
}
