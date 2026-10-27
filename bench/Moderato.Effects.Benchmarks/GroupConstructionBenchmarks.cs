using BenchmarkDotNet.Attributes;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class GroupConstructionBenchmarks
{
    [Params(0, 8)]
    public int Count;

    [Params(false, true)]
    public bool UseList;

    private IEnumerable<Effect<int, string, EmptyEnvironment>> effects = [];

    [GlobalSetup]
    public void Setup()
    {
        var values = new Effect<int, string, EmptyEnvironment>[Count];
        for (var i = 0; i < values.Length; i++) values[i] = Effect<EmptyEnvironment>.Succeed(i);
        effects = UseList ? values.ToList() : values;
    }

    [Benchmark] public Effect<int[], string, EmptyEnvironment> All() => Effect.All(effects);
    [Benchmark] public Effect<Exit<int, string>[], Never, EmptyEnvironment> AllSettled() => Effect.AllSettled(effects);
    [Benchmark] public Effect<int[], string[], EmptyEnvironment> AllErrors() => Effect.AllErrors(effects);
}
