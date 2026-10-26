using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

// The cost of an effect built once and run as Map → AndThen, on both paths.
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ChainBenchmarks
{
    [Params(false, true)]
    public bool Fail;

    private Effect<int, string, EmptyEnvironment> combinators;
    private Effect<int, string, EmptyEnvironment> gen;

    [GlobalSetup]
    public void Setup()
    {
        combinators = Source().Map(static x => x + 2).AndThen(static x => Half(x));
        gen = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var x = await scope.Run(Source());
            return await scope.Run(Half(x + 2));
        });

        // Every row must compute the same thing to measure the same path.
        int[] rows = [Result_(), AsyncResult().AsTask().Result, Effect_Combinators().AsTask().Result, Effect_Gen().AsTask().Result];
        if (rows.Distinct().Count() != 1) throw new InvalidOperationException("Benchmarks disagree: " + string.Join(", ", rows));
    }

    private Effect<int, string, EmptyEnvironment> Source() => Fail ? Effect<EmptyEnvironment>.Fail("failed") : Effect<EmptyEnvironment>.Succeed(20);

    private static Effect<int, string, EmptyEnvironment> Half(int x) => Effect<EmptyEnvironment>.Succeed(x / 2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Result<int, string> ResultSource(int x) => Fail ? Result.Err("failed") : Result.Ok(x);

    [Benchmark(Baseline = true, Description = "Result (sync)")]
    [BenchmarkCategory("Chain")]
    public int Result_() => ResultSource(20).Map(static x => x + 2).AndThen<int>(static x => Result.Ok(x / 2)).UnwrapOr(-1);

    [Benchmark(Description = "async method returning Result")]
    [BenchmarkCategory("Chain")]
    public async ValueTask<int> AsyncResult()
    {
        var source = await Task.FromResult(ResultSource(20)).ConfigureAwait(false);
        return source.Map(static x => x + 2).AndThen<int>(static x => Result.Ok(x / 2)).UnwrapOr(-1);
    }

    [Benchmark(Description = "Effect (combinators)")]
    [BenchmarkCategory("Chain")]
    public async ValueTask<int> Effect_Combinators() => (await combinators.RunAsync().ConfigureAwait(false)).UnwrapOr(-1);

    [Benchmark(Description = "Effect (Gen block)")]
    [BenchmarkCategory("Chain")]
    public async ValueTask<int> Effect_Gen() => (await gen.RunAsync().ConfigureAwait(false)).UnwrapOr(-1);
}
