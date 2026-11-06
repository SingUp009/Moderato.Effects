using BenchmarkDotNet.Attributes;
using Moderato.Effects.Testing;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ValueClockAllocationBenchmarks
{
    // 0: Sleep(0), 1: Retry eight attempts, 2: Repeat eight runs.
    [Params(0, 1, 2)]
    public int Operation;

    private Effect<int, string, ValueClock> effect;

    [GlobalSetup]
    public void Setup()
    {
        var scenario = ValueClockAllocationScenarios.Create(Operation);
        effect = scenario.Effect;
        if (Run().GetAwaiter().GetResult() != scenario.Expected) throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark]
    public ValueTask<Result<int, string>> Run() => effect.RunAsync(default);
}
