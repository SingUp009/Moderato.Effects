using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class SingleChildContextBenchmarks
{
    [Params(0, 100)]
    public int ContextCount;

    private Effect<int, Never, EmptyEnvironment> parent;

    [GlobalSetup]
    public void Setup()
    {
        var failure = new IOException("child");
        var cleanupFailure = new IOException("cleanup");
        var primary = Effect<EmptyEnvironment>.Sync<Unit>(() => throw failure);
        var cleanup = Effect<EmptyEnvironment>.Sync<Unit>(() => throw cleanupFailure);
        var child = Scope<Never, EmptyEnvironment>.Gen(scope =>
        {
            for (var i = 0; i < ContextCount; i++)
            {
#pragma warning disable MEFF305
                scope.Defer(_ => cleanup);
#pragma warning restore MEFF305
            }
            scope.Defer(_ => primary);
            return new ValueTask<int>(42);
        });
        parent = child.Fork().Use(static fiber => fiber.Join());
        if (!Run().AsTask().GetAwaiter().GetResult().TryGetValue(out Die? die)
            || !ReferenceEquals(die.Exception, failure)
            || die.Suppressed.Count != ContextCount
            || die.OmittedSuppressed != 0)
        {
            throw new InvalidOperationException("Unexpected child context.");
        }
    }

    [Benchmark] public ValueTask<Exit<int, Never>> Run() => parent.RunExitAsync(CancellationToken.None);
}
