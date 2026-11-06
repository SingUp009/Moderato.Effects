using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ResultAsyncBenchmarks
{
    [Params(false, true)] public bool Fail;

    private Effect<int, string, EmptyEnvironment> immediate;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        Result<int, string> result = Fail ? Result.Err("failed") : Result.Ok(42);
        immediate = Effect<EmptyEnvironment>.FromResultAsync(_ => new ValueTask<Result<int, string>>(result));
        suspended = Effect<EmptyEnvironment>.FromResultAsync(async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return result;
        });
        if (!Immediate().Result.Equals(result) || !Suspended().AsTask().GetAwaiter().GetResult().Equals(result))
            throw new InvalidOperationException("Unexpected result.");
    }

    [Benchmark] public ValueTask<Result<int, string>> Immediate() => immediate.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Suspended() => suspended.RunAsync();
}
