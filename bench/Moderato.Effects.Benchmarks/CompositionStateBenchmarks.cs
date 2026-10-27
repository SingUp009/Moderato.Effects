using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class CompositionStateBenchmarks
{
    [Params(0, 1, 2, 3)] public int Operation;

    private Effect<int, string, EmptyEnvironment> source;
    private Effect<int, string, EmptyEnvironment> prepared;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        source = Effect<EmptyEnvironment>.Succeed(20);
        Effect<int, string, EmptyEnvironment> pending = Effect<EmptyEnvironment>.Async<int>(static async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return 20;
        });
        prepared = Build();
        suspended = Compose(pending);
        var expected = Operation == 1 ? 22 : Operation == 3 ? 20 : 42;
        if (Run().Result.UnwrapOr(-1) != expected || Suspended().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != expected)
            throw new InvalidOperationException("Unexpected composition result.");
    }

    [Benchmark] public Effect<int, string, EmptyEnvironment> Build() => Compose(source);
    [Benchmark] public ValueTask<Result<int, string>> Run() => prepared.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Suspended() => suspended.RunAsync();

    private Effect<int, string, EmptyEnvironment> Compose(Effect<int, string, EmptyEnvironment> effect) => Operation switch
    {
        0 => effect.Map(static value => value + 22),
        1 => effect.AndThen(static _ => Effect<EmptyEnvironment>.Succeed(22)),
        2 => effect.Match(static value => value + 22, static _ => -1),
        _ => effect.UnwrapOr(99),
    };
}
