using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class LargeValueBenchmarks
{
    private Effect<Payload, Never, EmptyEnvironment> constant;
    private Effect<Payload, Never, EmptyEnvironment> deferred;
    private Effect<int, Never, EmptyEnvironment> mapped;

    [GlobalSetup]
    public void Setup()
    {
        constant = Effect<EmptyEnvironment>.Succeed(new Payload { Value = 42 });
        deferred = Effect<EmptyEnvironment>.Sync(static () => new Payload { Value = 42 });
        mapped = deferred.Map(static value => value.Value);
        if (Constant().Result.UnwrapOr(default).Value != 42
            || Deferred().Result.UnwrapOr(default).Value != 42
            || Mapped().Result.UnwrapOr(-1) != 42)
        {
            throw new InvalidOperationException("Unexpected benchmark result.");
        }
    }

    [Benchmark] public ValueTask<Result<Payload, Never>> Constant() => constant.RunAsync();
    [Benchmark] public ValueTask<Result<Payload, Never>> Deferred() => deferred.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> Mapped() => mapped.RunAsync();

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    public struct Payload
    {
        public int Value;
    }
}
