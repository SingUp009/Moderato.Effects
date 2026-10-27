using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ResourceReleaseBenchmarks
{
    private Effect<int, Never, EmptyEnvironment> use;
    private Effect<int, Never, EmptyEnvironment> gen;

    [GlobalSetup]
    public void Setup()
    {
        var value = Effect<EmptyEnvironment>.Succeed(42);
        var release = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        use = Resource.Make(value, (_, _) => release).Use(_ => value);
        gen = Scope<Never, EmptyEnvironment>.Gen(scope =>
        {
            scope.Defer(_ => release);
            return new ValueTask<int>(42);
        });

        if (Use().Result.UnwrapOr(-1) != 42 || Gen().Result.UnwrapOr(-1) != 42)
            throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark] public ValueTask<Result<int, Never>> Use() => use.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> Gen() => gen.RunAsync();
}
