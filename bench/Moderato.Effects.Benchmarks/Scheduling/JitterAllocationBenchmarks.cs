using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class JitterAllocationBenchmarks
{
    [Params(false, true)]
    public bool Jittered;

    private readonly ValueEnvironment valueEnvironment = new(TimeProvider.System, new Random(42));
    private readonly ReferenceEnvironment referenceEnvironment = new(TimeProvider.System, new Random(42));
    private Effect<int, string, ValueEnvironment> valueRetry;
    private Effect<int, string, ReferenceEnvironment> referenceRetry;
    private Effect<int, string, ValueEnvironment> valueRepeat;
    private Effect<int, string, ReferenceEnvironment> referenceRepeat;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, ValueEnvironment> valueFailure = Effect<ValueEnvironment>.Fail("expected");
        Effect<int, string, ReferenceEnvironment> referenceFailure = Effect<ReferenceEnvironment>.Fail("expected");
        Effect<int, string, ValueEnvironment> valueSuccess = Effect<ValueEnvironment>.Succeed(42);
        Effect<int, string, ReferenceEnvironment> referenceSuccess = Effect<ReferenceEnvironment>.Succeed(42);
        var plain = Schedule.Spaced(TimeSpan.Zero).UpTo(8);
        var jittered = plain.Jittered();
        valueRetry = Jittered ? valueFailure.Retry(jittered) : valueFailure.Retry(plain);
        referenceRetry = Jittered ? referenceFailure.Retry(jittered) : referenceFailure.Retry(plain);
        valueRepeat = Jittered ? valueSuccess.Repeat(jittered) : valueSuccess.Repeat(plain);
        referenceRepeat = Jittered ? referenceSuccess.Repeat(jittered) : referenceSuccess.Repeat(plain);
        if (!ValueRetry().Result.TryGetErr(out var valueError) || valueError != "expected"
            || !ReferenceRetry().Result.TryGetErr(out var referenceError) || referenceError != "expected"
            || !ValueRepeat().Result.TryGetOk(out var value) || value != 42
            || !ReferenceRepeat().Result.TryGetOk(out var reference) || reference != 42)
        {
            throw new InvalidOperationException("Unexpected recurrence result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, string>> ValueRetry() => valueRetry.RunAsync(valueEnvironment);
    [Benchmark] public ValueTask<Result<int, string>> ReferenceRetry() => referenceRetry.RunAsync(referenceEnvironment);
    [Benchmark] public ValueTask<Result<int, string>> ValueRepeat() => valueRepeat.RunAsync(valueEnvironment);
    [Benchmark] public ValueTask<Result<int, string>> ReferenceRepeat() => referenceRepeat.RunAsync(referenceEnvironment);

    private readonly record struct ValueEnvironment(TimeProvider Time, Random Random) : IHas<TimeProvider>, IHas<Random>
    {
        TimeProvider IHas<TimeProvider>.Service => Time;
        Random IHas<Random>.Service => Random;
    }

    private sealed record ReferenceEnvironment(TimeProvider Time, Random Random) : IHas<TimeProvider>, IHas<Random>
    {
        TimeProvider IHas<TimeProvider>.Service => Time;
        Random IHas<Random>.Service => Random;
    }
}
