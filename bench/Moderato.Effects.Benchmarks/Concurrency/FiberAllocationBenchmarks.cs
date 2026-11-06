using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class FiberAllocationBenchmarks
{
    private Effect<int, Never, EmptyEnvironment> joined;
    private Effect<int, Never, EmptyEnvironment> unjoined;
    private Effect<int, Never, EmptyEnvironment> many;
    private Effect<int, Never, EmptyEnvironment> defect;

    [GlobalSetup]
    public void Setup()
    {
        var child = Effect<EmptyEnvironment>.Async<int>(static async ct =>
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return 42;
        });
        joined = child.Fork().Use(static fiber => fiber.Join());
        unjoined = Effect<EmptyEnvironment>.Succeed(42).Fork().Use(static _ => Effect<EmptyEnvironment>.Succeed(42));
        many = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var total = 0;
            for (var i = 0; i < 8; i++)
            {
                var fiber = await scope.Fork(child);
                total += await scope.Run(fiber.Join());
            }

            return total;
        });

        var failure = new IOException("child failed");
        defect = Effect<EmptyEnvironment>.Sync<int>(() => throw failure).Fork().Use(static fiber => fiber.Join());
        if (!ChildDefect().AsTask().GetAwaiter().GetResult().TryGetValue(out Die? die)
            || !ReferenceEquals(die.Exception, failure) || die.Suppressed.Count != 0)
        {
            throw new InvalidOperationException("Unexpected child failure.");
        }

        if (TaskControl().GetAwaiter().GetResult() != 42
            || Joined().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42
            || Unjoined().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 42
            || EightJoined().AsTask().GetAwaiter().GetResult().UnwrapOr(-1) != 336)
        {
            throw new InvalidOperationException("Unexpected fiber result.");
        }
    }

    [Benchmark] public ValueTask<Exit<int, Never>> ChildDefect() => defect.RunExitAsync();
    [Benchmark] public Task<int> TaskControl() => Task.Run(static () => 42);
    [Benchmark] public ValueTask<Result<int, Never>> Joined() => joined.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> Unjoined() => unjoined.RunAsync();
    [Benchmark] public ValueTask<Result<int, Never>> EightJoined() => many.RunAsync();
}
