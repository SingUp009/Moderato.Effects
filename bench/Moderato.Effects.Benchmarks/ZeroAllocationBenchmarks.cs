using BenchmarkDotNet.Attributes;
using Moderato.Effects.Testing;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ZeroAllocationBenchmarks
{
    [ParamsAllValues]
    public AllocationCase Case;

    private Effect<int, string, EmptyEnvironment> effect;

    [GlobalSetup]
    public void Setup()
    {
        var scenario = AllocationScenarios.Create(Case);
        effect = scenario.Effect;
        var result = effect.RunAsync().GetAwaiter().GetResult();
        if (result != scenario.Expected) throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark]
    public ValueTask<Result<int, string>> Run() => effect.RunAsync();
}
