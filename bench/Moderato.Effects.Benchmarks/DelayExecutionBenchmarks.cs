using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class DelayExecutionBenchmarks
{
    [Params(0, 1)]
    public int SleepMilliseconds;

    private readonly ValueEnvironment valueEnvironment = new(TimeProvider.System);
    private readonly ReferenceEnvironment referenceEnvironment = new(TimeProvider.System);
    private Effect<int, TimeoutError, ValueEnvironment> value;
    private Effect<int, TimeoutError, ReferenceEnvironment> reference;

    private Effect<int, Never, ValueEnvironment> valueSleep;
    private Effect<int, Never, ReferenceEnvironment> referenceSleep;

    [GlobalSetup]
    public void Setup()
    {
        var delay = TimeSpan.FromMilliseconds(SleepMilliseconds);
        valueSleep = Effect.Sleep<ValueEnvironment>(delay).Map(static _ => 42);
        referenceSleep = Effect.Sleep<ReferenceEnvironment>(delay).Map(static _ => 42);
        value = valueSleep.Timeout(TimeSpan.FromMinutes(1));
        reference = referenceSleep.Timeout(TimeSpan.FromMinutes(1));
        if (!ValueSleep().AsTask().GetAwaiter().GetResult().TryGetOk(out var c) || c != 42
            || !ReferenceSleep().AsTask().GetAwaiter().GetResult().TryGetOk(out var d) || d != 42
            || !ValueEnvironmentRun().AsTask().GetAwaiter().GetResult().TryGetOk(out var a) || a != 42
            || !ReferenceEnvironmentRun().AsTask().GetAwaiter().GetResult().TryGetOk(out var b) || b != 42)
        {
            throw new InvalidOperationException("Unexpected timeout result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, Never>> ValueSleep() => valueSleep.RunAsync(valueEnvironment);
    [Benchmark] public ValueTask<Result<int, Never>> ReferenceSleep() => referenceSleep.RunAsync(referenceEnvironment);
    [Benchmark] public ValueTask<Result<int, TimeoutError>> ValueEnvironmentRun() => value.RunAsync(valueEnvironment);
    [Benchmark] public ValueTask<Result<int, TimeoutError>> ReferenceEnvironmentRun() => reference.RunAsync(referenceEnvironment);

    private readonly record struct ValueEnvironment(TimeProvider Clock) : IHas<TimeProvider>
    {
        public TimeProvider Service => Clock;
    }

    private sealed record ReferenceEnvironment(TimeProvider Clock) : IHas<TimeProvider>
    {
        public TimeProvider Service => Clock;
    }
}
