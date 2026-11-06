using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ScheduleWaitBenchmarks
{
    [Params(false, true)] public bool Wait { get; set; }
    private Effect<int, string, DefaultEnvironment> retry;
    private Effect<int, string, DefaultEnvironment> repeat;

    [GlobalSetup]
    public void Setup()
    {
        var schedule = Schedule.Spaced(Wait ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero).UpTo(8);
        Effect<int, string, DefaultEnvironment> failing = Effect<DefaultEnvironment>.Fail("last");
        Effect<int, string, DefaultEnvironment> succeeding = Effect<DefaultEnvironment>.Succeed(42);
        retry = failing.Retry(schedule);
        repeat = succeeding.Repeat(schedule);
        if (!Retry().AsTask().GetAwaiter().GetResult().Equals((Result<int, string>)Result.Err("last"))
            || Repeat().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42)
        {
            throw new InvalidOperationException("Unexpected schedule result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, string>> Retry() => retry.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Repeat() => repeat.RunAsync();
}
