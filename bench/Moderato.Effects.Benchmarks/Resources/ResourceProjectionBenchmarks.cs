using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ResourceProjectionBenchmarks
{
    private Effect<int, string, EmptyEnvironment> control;
    private Effect<int, string, EmptyEnvironment> query;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, EmptyEnvironment> one = Effect<EmptyEnvironment>.Succeed(1);
        Effect<int, string, EmptyEnvironment> value = Effect<EmptyEnvironment>.Succeed(42);
        Effect<int, string, EmptyEnvironment> delayed = Effect<EmptyEnvironment>.Async<int>(static async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return 42;
        });
        var first = Resource.Make(one, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var second = Resource.Make(value, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var pending = Resource.Make(delayed, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        control = first.AndThen(_ => second).Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        query = (from a in first from b in second select b).Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        suspended = (from a in first from b in pending select b).Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        if (AndThen().Result.UnwrapOr(-1) != 42 || SelectMany().Result.UnwrapOr(-1) != 42
            || SuspendedSelectMany().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42)
        {
            throw new InvalidOperationException("Unexpected resource projection result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, string>> AndThen() => control.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> SelectMany() => query.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> SuspendedSelectMany() => suspended.RunAsync();
}
