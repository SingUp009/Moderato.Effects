using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ResourceAcquisitionBenchmarks
{
    private Effect<int, string, EmptyEnvironment> use;
    private Effect<int, string, EmptyEnvironment> mapped;
    private Effect<int, int, EmptyEnvironment> mappedErrorSuccess;
    private Effect<int, int, EmptyEnvironment> mappedErrorFailure;
    private Effect<int, string, EmptyEnvironment> failed;
    private Effect<int, string, EmptyEnvironment> suspended;

    [GlobalSetup]
    public void Setup()
    {
        Effect<int, string, EmptyEnvironment> success = Effect<EmptyEnvironment>.Succeed(42);
        Effect<int, string, EmptyEnvironment> failure = Effect<EmptyEnvironment>.Fail("failure");
        var resource = Resource.Make(success, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var failing = Resource.Make(failure, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        use = resource.Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        mapped = resource.Map(static value => value + 1).Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        mappedErrorSuccess = resource.MapErr(static error => error.Length).Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        mappedErrorFailure = failing.MapErr(static error => error.Length).Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        failed = failing.Use(static value => Effect<EmptyEnvironment>.Succeed(value));
        Effect<int, string, EmptyEnvironment> delayed = Effect<EmptyEnvironment>.Async<int>(static async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return 42;
        });
        suspended = Resource.Make(delayed, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))
            .Map(static value => value + 1).Use(static value => Effect<EmptyEnvironment>.Succeed(value));

        if (Use().Result.UnwrapOr(-1) != 42 || Map().Result.UnwrapOr(-1) != 43
            || MapErrSuccess().Result.UnwrapOr(-1) != 42
            || !MapErrFailure().Result.TryGetErr(out var error) || error != 7
            || !Failed().Result.TryGetErr(out var original) || original != "failure"
            || SuspendedMap().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 43)
        {
            throw new InvalidOperationException("Unexpected resource acquisition result.");
        }
    }

    [Benchmark] public ValueTask<Result<int, string>> Use() => use.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Map() => mapped.RunAsync();
    [Benchmark] public ValueTask<Result<int, int>> MapErrSuccess() => mappedErrorSuccess.RunAsync();
    [Benchmark] public ValueTask<Result<int, int>> MapErrFailure() => mappedErrorFailure.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> Failed() => failed.RunAsync();
    [Benchmark] public ValueTask<Result<int, string>> SuspendedMap() => suspended.RunAsync();
}
