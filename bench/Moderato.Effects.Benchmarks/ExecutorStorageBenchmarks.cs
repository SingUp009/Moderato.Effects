using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
[InvocationCount(1, 1)]
public class ExecutorStorageBenchmarks
{
    [Params(1, 64, 1024, 100_000)]
    public int Length;

    private Effect<int, Never, EmptyEnvironment> mapped;
    private Effect<int, Never, EmptyEnvironment> bound;
    private Effect<int, Never, EmptyEnvironment> resource;

    [GlobalSetup]
    public void Setup()
    {
        mapped = BuildMap();
        bound = BuildBind();
        resource = BuildResource().Use(Effect<EmptyEnvironment>.Succeed);
        if (RunMap().Result.UnwrapOr(-1) != Length || RunBind().Result.UnwrapOr(-1) != Length
            || RunResource().Result.UnwrapOr(-1) != Length)
        {
            throw new InvalidOperationException("Executor storage benchmarks disagree.");
        }
    }

    [Benchmark]
    public Effect<int, Never, EmptyEnvironment> BuildMap()
    {
        var effect = Effect<EmptyEnvironment>.Succeed(0);
        for (var i = 0; i < Length; i++) effect = effect.Map(static value => value + 1);
        return effect;
    }

    [Benchmark]
    public Effect<int, Never, EmptyEnvironment> BuildBind()
    {
        var effect = Effect<EmptyEnvironment>.Succeed(0);
        for (var i = 0; i < Length; i++) effect = effect.AndThen(static value => Effect<EmptyEnvironment>.Succeed(value + 1));
        return effect;
    }

    [Benchmark]
    public Resource<int, Never, EmptyEnvironment> BuildResource()
    {
        var resource = Make(0);
        for (var i = 0; i < Length; i++)
        {
            var next = Make(i + 1);
            resource = resource.AndThen(_ => next);
        }
        return resource;

        static Resource<int, Never, EmptyEnvironment> Make(int value)
            => Resource.Make(Effect<EmptyEnvironment>.Succeed(value), static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
    }

    [Benchmark] public ValueTask<Result<int, Never>> RunMap() => mapped.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> RunBind() => bound.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> RunResource() => resource.RunAsync();
}
