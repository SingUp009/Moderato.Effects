using BenchmarkDotNet.Attributes;
using Moderato.Effects.Testing;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class SchedulingAllocationBenchmarks
{
    [ParamsAllValues]
    public SchedulingAllocationCase Case;

    private Effect<int, string, EmptyEnvironment> effect;

    [GlobalSetup]
    public void Setup()
    {
        var scenario = SchedulingAllocationScenarios.Create(Case);
        effect = scenario.Effect;
        if (Run().GetAwaiter().GetResult() != scenario.Expected) throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark]
    public ValueTask<Result<int, string>> Run() => effect.RunAsync();
}
