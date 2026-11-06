using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

// Built once and returned as a ValueTask, so only the run itself is measured.
[MemoryDiagnoser]
public class ExecutionBenchmarks
{
    private Effect<int, Never, int> succeed;
    private Effect<int, Never, int> sync;
    private Effect<int, Never, int> access;
    private Effect<int, Never, int> completedAsync;
    private Effect<int, Never, int> accessAsync;
    private Effect<int, string, int> tryAsync;
    private Effect<int, string, int> fail;
    private Effect<int, Never, int> mapped;
    private Effect<int, Never, int> bound;
    private Effect<int, Never, int> through;
    private readonly CancellationTokenSource cancellation = new();

    [GlobalSetup]
    public void Setup()
    {
        succeed = Effect<int>.Succeed(42);
        sync = Effect<int>.Sync(static () => 42);
        access = Effect<int>.Access(static env => env);
        completedAsync = Effect<int>.Async(static _ => new ValueTask<int>(42));
        accessAsync = Effect<int>.AccessAsync(static (env, _) => new ValueTask<int>(env));
        tryAsync = Effect<int>.TryAsync(static _ => new ValueTask<int>(42), static ex => ex.Message);
        fail = Effect<int>.Fail("failed");
        mapped = succeed.Map(static value => value + 1);
        // Reuse a step to distinguish continuation costs from effect construction.
        bound = succeed.AndThen(_ => succeed);
        through = succeed.AndThrough(_ => succeed);

        if (Succeed().Result.UnwrapOr(-1) != 42 || Sync().Result.UnwrapOr(-1) != 42
            || Access().Result.UnwrapOr(-1) != 42 || CompletedAsync().Result.UnwrapOr(-1) != 42
            || AccessAsync().Result.UnwrapOr(-1) != 42 || TryAsync().Result.UnwrapOr(-1) != 42
            || !Fail().Result.TryGetErr(out _) || Map().Result.UnwrapOr(-1) != 43
            || AndThen().Result.UnwrapOr(-1) != 42 || AndThrough().Result.UnwrapOr(-1) != 42
            || SucceedWithCancellation().Result.UnwrapOr(-1) != 42)
        {
            throw new InvalidOperationException("Unexpected benchmark result.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => cancellation.Dispose();

    [Benchmark] public ValueTask<Result<int, Never>> Succeed() => succeed.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> Sync() => sync.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> Access() => access.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> CompletedAsync() => completedAsync.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> AccessAsync() => accessAsync.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, string>> TryAsync() => tryAsync.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, string>> Fail() => fail.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> Map() => mapped.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> AndThen() => bound.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> AndThrough() => through.RunAsync(42);
    [Benchmark] public ValueTask<Result<int, Never>> SucceedWithCancellation() => succeed.RunAsync(42, cancellation.Token);
}
