using BenchmarkDotNet.Attributes;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class FailureProvenanceBenchmarks
{
    private Effect<int, string, EmptyEnvironment> constant;
    private Effect<int, string, EmptyEnvironment> mapped;
    private Effect<int, string, EmptyEnvironment> gen;

    [GlobalSetup]
    public void Setup()
    {
        constant = Effect<EmptyEnvironment>.Fail("failure");
        mapped = constant.MapErr(static error => error);
        var source = constant;
        gen = Scope<string, EmptyEnvironment>.Gen<int>(async scope => await scope.Run(source));
        foreach (var exit in new[] { Constant().Result, Mapped().Result, Gen().Result })
        {
            if (!exit.TryGetValue(out Moderato.Results.Err<string> error) || error.Error != "failure")
                throw new InvalidOperationException("Unexpected failure.");
        }
    }

    [Benchmark] public ValueTask<Exit<int, string>> Constant() => constant.RunExitAsync();
    [Benchmark] public ValueTask<Exit<int, string>> Mapped() => mapped.RunExitAsync();
    [Benchmark] public ValueTask<Exit<int, string>> Gen() => gen.RunExitAsync();
}
