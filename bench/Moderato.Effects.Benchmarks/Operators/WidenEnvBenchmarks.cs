using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class WidenEnvBenchmarks
{
    [Params(false, true)] public bool Deferred { get; set; }
    [Params(false, true)] public bool Failure { get; set; }

    private Effect<int, string, object> source;
    private Effect<int, string, string> widened;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, object> constant = Failure
            ? Effect<object>.Fail("error")
            : Effect<object>.Succeed(42);
        source = Deferred ? constant.Map(static value => value) : constant;
        widened = source.WidenEnv<string>();
    }

    [Benchmark] public Effect<int, string, string> Construct() => source.WidenEnv<string>();
    [Benchmark] public ValueTask<Result<int, string>> Execute() => widened.RunAsync("environment", CancellationToken.None);
}
