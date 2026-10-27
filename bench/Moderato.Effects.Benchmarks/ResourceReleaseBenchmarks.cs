using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ResourceReleaseBenchmarks
{
    private Effect<int, Never, EmptyEnvironment> use;
    private Effect<int, Never, EmptyEnvironment> gen;

    private Effect<int, Never, EmptyEnvironment> empty;
    private Effect<int, Never, EmptyEnvironment> defects;

    private Effect<int, Never, EmptyEnvironment> observed;
    private Effect<int, Never, EmptyEnvironment> pending;

    [GlobalSetup]
    public void Setup()
    {
        var value = Effect<EmptyEnvironment>.Succeed(42);
        var release = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        observed = Scope<Never, EmptyEnvironment>.Gen(async scope => await scope.Run(value));
        var delayed = Effect<EmptyEnvironment>.Async<int>(static async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return 42;
        });
        pending = Scope<Never, EmptyEnvironment>.Gen(async scope => await scope.Run(delayed));
        use = Resource.Make(value, (_, _) => release).Use(_ => value);
        gen = Scope<Never, EmptyEnvironment>.Gen(scope =>
        {
            scope.Defer(_ => release);
            return new ValueTask<int>(42);
        });

        empty = Scope<Never, EmptyEnvironment>.Gen<int>(static _ => new(42));
        var exception = new IOException("release failed");
        var fail = Effect<EmptyEnvironment>.Sync<Unit>(() => throw exception);
        Func<ExitCase, Effect<Unit, Never, EmptyEnvironment>> cleanup = _ => fail;
        defects = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(cleanup);
            scope.Defer(cleanup);
            scope.Defer(cleanup);
            return new(42);
        });

        if (ObservedStep().Result.UnwrapOr(-1) != 42 || PendingStep().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42
            || EmptyGen().Result.UnwrapOr(-1) != 42
            || !FailingReleases().Result.TryGetValue(out Die? die) || die.Suppressed.Count != 2)
        {
            throw new InvalidOperationException("Unexpected release context.");
        }

        if (Use().Result.UnwrapOr(-1) != 42 || Gen().Result.UnwrapOr(-1) != 42)
            throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark] public ValueTask<Result<int, Never>> ObservedStep() => observed.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> PendingStep() => pending.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> EmptyGen() => empty.RunAsync();
    [Benchmark] public ValueTask<Exit<int, Never>> FailingReleases() => defects.RunExitAsync();
    [Benchmark] public ValueTask<Result<int, Never>> Use() => use.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> Gen() => gen.RunAsync();
}
