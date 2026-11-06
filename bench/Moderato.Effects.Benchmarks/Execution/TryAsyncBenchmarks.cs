using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class TryAsyncBenchmarks
{
    [Params(false, true)] public bool Fail;
    private Effect<int, string, EmptyEnvironment> immediate;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        var failure = new IOException("failed");
        immediate = Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(_ =>
        {
            if (Fail) throw failure;
            return new(42);
        }, static ex => ex.Message);
        suspended = Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            if (Fail) throw failure;
            return 42;
        }, static ex => ex.Message);
        Result<int, string> expected = Fail ? Result.Err("failed") : Result.Ok(42);
        if (!Immediate().Result.Equals(expected) || !Suspended().AsTask().GetAwaiter().GetResult().Equals(expected))
            throw new InvalidOperationException("Unexpected result.");
    }

    [Benchmark] public ValueTask<Result<int, string>> Immediate() => immediate.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Suspended() => suspended.RunAsync();
}
