using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class CallbackFailureBenchmarks
{
    private readonly IOException bodyFailure = new("body failed");
    private readonly IOException callbackFailure = new("callback failed");
    private Effect<int, Never, CancellationTokenSource> direct;
    private Effect<int, Never, CancellationTokenSource> nested;
    private Effect<int, Never, CancellationTokenSource> control;

    [GlobalSetup]
    public void Setup()
    {
        direct = Effect<CancellationTokenSource>.AccessAsync<int>((caller, ct) =>
        {
            using var registration = ct.Register(static state => throw (Exception)state!, callbackFailure);
            caller.Cancel();
            throw bodyFailure;
        });
        nested = Resource.Make(Effect<CancellationTokenSource>.Succeed(Unit.Value),
                static (_, _) => Effect<CancellationTokenSource>.Succeed(Unit.Value))
            .Use(_ => direct);
        control = Effect<CancellationTokenSource>.AccessAsync<int>((caller, ct) =>
        {
            using var registration = ct.Register(static () => { });
            caller.Cancel();
            throw bodyFailure;
        });

        foreach (var effect in new[] { direct, nested })
        {
            if (!Run(effect).TryGetValue(out Die? die) || !ReferenceEquals(die.Exception, bodyFailure)
                || die.Suppressed.Count != 1 || die.Suppressed[0].Case.Value is not Die callback
                || !ReferenceEquals(callback.Exception, callbackFailure))
            {
                throw new InvalidOperationException("Unexpected callback failure context.");
            }
        }

        if (!Control().TryGetValue(out Die? original) || !ReferenceEquals(original.Exception, bodyFailure) || original.Suppressed.Count != 0)
        {
            throw new InvalidOperationException("Unexpected control result.");
        }
    }

    [Benchmark] public Exit<int, Never> Direct() => Run(direct);
    [Benchmark] public Exit<int, Never> Nested() => Run(nested);
    [Benchmark] public Exit<int, Never> Control() => Run(control);

    private static Exit<int, Never> Run(Effect<int, Never, CancellationTokenSource> effect)
    {
        using var caller = new CancellationTokenSource();
        return effect.RunExitAsync(caller, caller.Token).GetAwaiter().GetResult();
    }
}
