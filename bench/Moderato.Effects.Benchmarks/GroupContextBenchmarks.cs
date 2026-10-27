using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class GroupContextBenchmarks
{
    private Effect<int[], string, EmptyEnvironment> all;
    private Effect<int[], string[], EmptyEnvironment> allErrors;
    private Effect<int[], string[], EmptyEnvironment> defect;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, EmptyEnvironment>[] successes = [.. Enumerable.Range(0, 8).Select(static value => (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(value))];
        Effect<int, string, EmptyEnvironment>[] errors = [.. Enumerable.Range(0, 8).Select(static _ => (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail("expected"))];
        var withDefect = errors.ToArray();
        var failure = new IOException("defect");
        withDefect[^1] = Effect<EmptyEnvironment>.Sync<int>(() => throw failure);
        all = Effect.All(successes);
        allErrors = Effect.AllErrors(errors);
        defect = Effect.AllErrors(withDefect);
        if (!All().Result.TryGetOk(out var values) || values.Length != 8
            || !AllErrors().Result.TryGetErr(out var failures) || failures.Length != 8
            || !WithDefect().Result.TryGetValue(out Die? die) || die.Suppressed.Count != 7)
        {
            throw new InvalidOperationException("Unexpected group result.");
        }
    }

    [Benchmark] public ValueTask<Result<int[], string>> All() => all.RunAsync();
    [Benchmark] public ValueTask<Result<int[], string[]>> AllErrors() => allErrors.RunAsync();
    [Benchmark] public ValueTask<Exit<int[], string[]>> WithDefect() => defect.RunExitAsync();
}
