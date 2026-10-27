using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class GroupContextBenchmarks
{
    private Effect<int[], string, EmptyEnvironment> all;
    private Effect<int[], string[], EmptyEnvironment> allErrors;
    private Effect<int[], string[], EmptyEnvironment> defect;

    private Effect<Exit<int, string>[], Never, EmptyEnvironment> settled;
    private Effect<int[], string[], EmptyEnvironment> mixed;

    private Effect<int, string, EmptyEnvironment> race;
    private Effect<int, string, EmptyEnvironment> raceFirst;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, EmptyEnvironment>[] successes = [.. Enumerable.Range(0, 8).Select(static value => (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(value))];
        Effect<int, string, EmptyEnvironment>[] errors = [.. Enumerable.Range(0, 8).Select(static _ => (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail("expected"))];
        var withDefect = errors.ToArray();
        var failure = new IOException("defect");
        withDefect[^1] = Effect<EmptyEnvironment>.Sync<int>(() => throw failure);
        race = Effect.Race(successes[0], successes[1]);
        raceFirst = Effect.RaceFirst(successes[0], successes[1]);
        settled = Effect.AllSettled(successes);
        mixed = Effect.AllErrors([successes[0], errors[0], successes[1], errors[1], successes[2], errors[2], successes[3], errors[3]]);
        all = Effect.All(successes);
        allErrors = Effect.AllErrors(errors);
        defect = Effect.AllErrors(withDefect);
        if (!Race().AsTask().GetAwaiter().GetResult().TryGetOk(out var winner) || winner is < 0 or > 1
            || !RaceFirst().AsTask().GetAwaiter().GetResult().TryGetOk(out var first) || first is < 0 or > 1
            || !Settled().Result.TryGetOk(out var exits) || exits.Length != 8
            || !MixedErrors().Result.TryGetErr(out var mixedFailures) || mixedFailures.Length != 4
            || !All().Result.TryGetOk(out var values) || values.Length != 8
            || !AllErrors().Result.TryGetErr(out var failures) || failures.Length != 8
            || !WithDefect().Result.TryGetValue(out Die? die) || die.Suppressed.Count != 7)
        {
            throw new InvalidOperationException("Unexpected group result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, string>> Race() => race.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> RaceFirst() => raceFirst.RunAsync();
    [Benchmark] public ValueTask<Result<Exit<int, string>[], Never>> Settled() => settled.RunAsync();
    [Benchmark] public ValueTask<Result<int[], string[]>> MixedErrors() => mixed.RunAsync();
    [Benchmark] public ValueTask<Result<int[], string>> All() => all.RunAsync();
    [Benchmark] public ValueTask<Result<int[], string[]>> AllErrors() => allErrors.RunAsync();
    [Benchmark] public ValueTask<Exit<int[], string[]>> WithDefect() => defect.RunExitAsync();
}
