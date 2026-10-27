using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class CancellationStorageBenchmarks
{
    private readonly CancellationTokenSource caller = new();
    private Effect<int, Never, EmptyEnvironment> effect;
    private Effect<int, Never, EmptyEnvironment> empty;
    private Effect<int, Never, EmptyEnvironment> nested;

    [GlobalSetup]
    public void Setup()
    {
        effect = Effect<EmptyEnvironment>.Async<int>(static _ => new ValueTask<int>(42));
        empty = Scope<Never, EmptyEnvironment>.Gen<int>(static _ => new(42));
        nested = Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value),
            static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)).Use(_ => effect);
        if (WithoutCaller().Result.UnwrapOr(-1) != 42 || WithCaller().Result.UnwrapOr(-1) != 42
            || EmptyGen().Result.UnwrapOr(-1) != 42 || NestedUse().Result.UnwrapOr(-1) != 42)
        {
            throw new InvalidOperationException("Unexpected cancellation result.");
        }
    }

    [GlobalCleanup] public void Cleanup() => caller.Dispose();
    [Benchmark] public ValueTask<Result<int, Never>> WithoutCaller() => effect.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> WithCaller() => effect.RunAsync(caller.Token);
    [Benchmark] public ValueTask<Result<int, Never>> EmptyGen() => empty.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> NestedUse() => nested.RunAsync(caller.Token);
}
