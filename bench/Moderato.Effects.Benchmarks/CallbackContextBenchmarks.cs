using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class CallbackContextBenchmarks
{
    [Params(0, 8, 100)] public int Count { get; set; }
    private readonly IOException bodyFailure = new("body");
    private readonly IOException callbackFailure = new("callback");
    private readonly IOException cleanupFailure = new("cleanup");
    private Effect<int, Never, CancellationTokenSource> effect;

    [GlobalSetup]
    public void Setup()
    {
        var register = Effect<CancellationTokenSource>.Async<Unit>(ct =>
        {
            _ = ct.Register(static state => throw (Exception)state!, callbackFailure);
            return new(Unit.Value);
        });
        var cleanup = Effect<CancellationTokenSource>.Sync<Unit>(() => throw cleanupFailure);
        var body = Scope<Never, CancellationTokenSource>.Gen<int>(scope =>
        {
            for (var i = 0; i < Count; i++) scope.Defer(_ => cleanup);
            throw bodyFailure;
        });
        var resource = Resource.Make(Effect<CancellationTokenSource>.Succeed(Unit.Value), static (_, _) =>
            Effect<CancellationTokenSource>.Access(caller => { caller.Cancel(); return Unit.Value; }));
        effect = resource.Use(_ => register.AndThen(_ => body));
        var exit = Run();
        if (!exit.TryGetValue(out Die? die) || !ReferenceEquals(die.Exception, bodyFailure)
            || die.Suppressed.Count != Math.Min(100, Count + 1)
            || die.OmittedSuppressed != Math.Max(0, Count - 99)
            || die.Suppressed[0].Case.Value is not Die callback || !ReferenceEquals(callback.Exception, callbackFailure))
        {
            throw new InvalidOperationException("Unexpected callback context.");
        }
    }

    [Benchmark]
    public Exit<int, Never> Run()
    {
        using var caller = new CancellationTokenSource();
        return effect.RunExitAsync(caller, caller.Token).GetAwaiter().GetResult();
    }
}
