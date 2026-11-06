using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class LinqAdapterBenchmarks
{
    [Params(0, 1, 2, 3)] public int Shape;

    private Effect<int, Never, EmptyEnvironment> first;
    private Effect<int, Never, EmptyEnvironment> second;
    private Effect<int, Never, EmptyEnvironment> pending;
    private Effect<int, string, EmptyEnvironment> prepared;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        first = Effect<EmptyEnvironment>.Succeed(20);
        second = Effect<EmptyEnvironment>.Succeed(22);
        pending = Effect<EmptyEnvironment>.Async<int>(static async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return 22;
        });
        prepared = Build();
        suspended = Query(pending);
        if (Run().Result.UnwrapOr(-1) != 42 || Suspended().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42)
            throw new InvalidOperationException("Unexpected LINQ result.");
    }

    [Benchmark] public Effect<int, string, EmptyEnvironment> Build() => Query(second);
    [Benchmark] public ValueTask<Result<int, string>> Run() => prepared.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Suspended() => suspended.RunAsync();

    private Effect<int, string, EmptyEnvironment> Query(Effect<int, Never, EmptyEnvironment> next)
    {
        Effect<int, string, EmptyEnvironment> typedFirst = first;
        Effect<int, string, EmptyEnvironment> typedNext = next;
        return Shape switch
        {
            0 => from a in typedFirst from b in typedNext select a + b,
            1 => from a in typedFirst from b in next select a + b,
            2 => from a in first from b in typedNext select a + b,
            _ => from a in first from b in next select a + b,
        };
    }
}
