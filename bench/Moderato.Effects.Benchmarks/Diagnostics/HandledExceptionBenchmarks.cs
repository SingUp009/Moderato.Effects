using BenchmarkDotNet.Attributes;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class HandledExceptionBenchmarks
{
    [Params(false, true)] public bool MapperThrows;
    private Effect<int, string, EmptyEnvironment> sync;
    private Effect<int, string, EmptyEnvironment> immediate;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        var failure = new IOException("body");
        var mapperFailure = new InvalidOperationException("mapper");
        string Map(IOException _)
        {
            if (MapperThrows) throw mapperFailure;
            return "handled";
        }
        sync = Effect<EmptyEnvironment>.Try<IOException, int, string>(() => throw failure, Map);
        immediate = Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(_ => throw failure, Map);
        suspended = Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            throw failure;
        }, Map);
        foreach (var effect in new[] { sync, immediate, suspended })
        {
            var exit = effect.RunExitAsync().AsTask().GetAwaiter().GetResult();
            if (MapperThrows ? !exit.TryGetValue(out Die? die) || !ReferenceEquals(die.Exception, mapperFailure)
                : !exit.TryGetValue(out Moderato.Results.Err<string> err) || err.Error != "handled")
            {
                throw new InvalidOperationException("Unexpected outcome.");
            }
        }
    }

    [Benchmark] public ValueTask<Exit<int, string>> Sync() => sync.RunExitAsync();
    [Benchmark] public ValueTask<Exit<int, string>> ImmediateAsync() => immediate.RunExitAsync();
    [Benchmark] public ValueTask<Exit<int, string>> SuspendedAsync() => suspended.RunExitAsync();
}
