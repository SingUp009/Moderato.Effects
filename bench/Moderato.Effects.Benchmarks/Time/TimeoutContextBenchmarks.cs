using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Time.Testing;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class TimeoutContextBenchmarks
{
    private readonly Clock clock = new();
    private Effect<int, TimeoutError, Clock> expired;
    private Effect<int, TimeoutError, Clock> completed;
    private Effect<int, TimeoutError, Clock> interrupted;

    [GlobalSetup]
    public void Setup()
    {
        var failure = new IOException("release failed");
        var resource = Resource.Make(Effect<Clock>.Succeed(42), (_, _) => Effect<Clock>.Sync<Unit>(() => throw failure));
        var body = Effect<Clock>.Access(env =>
        {
            env.Time.Advance(TimeSpan.FromSeconds(1));
            return 42;
        });
        expired = resource.Use(_ => body).Timeout(TimeSpan.FromSeconds(1));
        completed = Effect<Clock>.Succeed(42).Timeout(TimeSpan.FromSeconds(1));
        var cancellation = new OperationCanceledException();
        var canceledBody = Effect<Clock>.Access<int>(env =>
        {
            env.Time.Advance(TimeSpan.FromSeconds(1));
            throw cancellation;
        });
        interrupted = resource.Use(_ => canceledBody).Timeout(TimeSpan.FromSeconds(1));
        if (!InterruptedContext().Result.TryGetValue(out Die? interruptedDie)
            || !ReferenceEquals(interruptedDie.Exception, failure) || interruptedDie.Suppressed.Count != 1
            || interruptedDie.Suppressed[0].Case.Value is not Failed { Error: TimeoutError })
        {
            throw new InvalidOperationException("Unexpected interrupted context.");
        }
        if (!IndependentDefect().Result.TryGetValue(out Die? die) || !ReferenceEquals(die.Exception, failure)
            || die.Suppressed.Count != 1 || die.Suppressed[0].Case.Value is not Failed { Error: TimeoutError }
            || !Completed().Result.TryGetValue(out Ok<int> ok) || ok.Value != 42)
        {
            throw new InvalidOperationException("Unexpected timeout context.");
        }
    }

    [Benchmark] public ValueTask<Exit<int, TimeoutError>> IndependentDefect() => expired.RunExitAsync(clock);
    [Benchmark] public ValueTask<Exit<int, TimeoutError>> InterruptedContext() => interrupted.RunExitAsync(clock);
    [Benchmark] public ValueTask<Exit<int, TimeoutError>> Completed() => completed.RunExitAsync(clock);

    public sealed class Clock : IHas<TimeProvider>
    {
        public FakeTimeProvider Time { get; } = new();
        public TimeProvider Service => Time;
    }
}
