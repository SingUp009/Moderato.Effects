using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class RecurrenceAwaitBenchmarks
{
    [Params(0, 1, 2)] public int WaitAt;
    private Effect<int, string, EmptyEnvironment> retry;
    private Effect<int, string, EmptyEnvironment> repeat;

    [GlobalSetup]
    public void Setup()
    {
        var failing = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            if (WaitAt == 1) await Task.Delay(1, ct).ConfigureAwait(false);
            return Result.Err("last");
        });
        Effect<int, string, EmptyEnvironment> succeeding = Effect<EmptyEnvironment>.Async(async ct =>
        {
            if (WaitAt == 1) await Task.Delay(1, ct).ConfigureAwait(false);
            return 42;
        });
        var stop = Effect<EmptyEnvironment>.Async(async ct =>
        {
            if (WaitAt == 2) await Task.Delay(1, ct).ConfigureAwait(false);
            return false;
        });
        retry = failing.Retry(_ => stop);
        repeat = succeeding.Repeat(_ => stop);
        if (!Retry().AsTask().GetAwaiter().GetResult().Equals((Result<int, string>)Result.Err("last"))
            || Repeat().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42)
        {
            throw new InvalidOperationException("Unexpected recurrence result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, string>> Retry() => retry.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Repeat() => repeat.RunAsync();
}
