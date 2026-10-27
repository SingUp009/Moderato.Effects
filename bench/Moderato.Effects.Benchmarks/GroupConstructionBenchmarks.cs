using BenchmarkDotNet.Attributes;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class GroupConstructionBenchmarks
{
    [Params(0, 8)]
    public int Count;

    private Effect<int, string, EmptyEnvironment>[] effects = [];

    [GlobalSetup]
    public void Setup()
    {
        effects = new Effect<int, string, EmptyEnvironment>[Count];
        for (var i = 0; i < effects.Length; i++) effects[i] = Effect<EmptyEnvironment>.Succeed(i);
    }

    [Benchmark] public Effect<int[], string, EmptyEnvironment> All() => Effect.All(effects);
    [Benchmark] public Effect<Exit<int, string>[], Never, EmptyEnvironment> AllSettled() => Effect.AllSettled(effects);
    [Benchmark] public Effect<int[], string[], EmptyEnvironment> AllErrors() => Effect.AllErrors(effects);
}
