using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ScalingBenchmarks
{
    [Params(1, 8, 32)]
    public int Length;

    private Effect<int, Never, EmptyEnvironment> mapped;
    private Effect<int, Never, EmptyEnvironment> bound;

    [GlobalSetup]
    public void Setup()
    {
        mapped = Effect<EmptyEnvironment>.Succeed(42);
        bound = mapped;
        var step = mapped;
        for (var i = 0; i < Length; i++)
        {
            mapped = mapped.Map(static x => x + 1);
            bound = bound.AndThen(_ => step);
        }

        if (MapChain().Result.UnwrapOr(-1) != 42 + Length || BindChain().Result.UnwrapOr(-1) != 42)
            throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark] public ValueTask<Result<int, Never>> MapChain() => mapped.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> BindChain() => bound.RunAsync();
}
