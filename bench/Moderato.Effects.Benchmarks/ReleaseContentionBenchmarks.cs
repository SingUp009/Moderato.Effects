using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ReleaseContentionBenchmarks
{
    [Params(2, 32)]
    public int Count;

    private Effect<int, Never, EmptyEnvironment> parent;

    [GlobalSetup]
    public void Setup()
    {
        var failures = Enumerable.Range(0, Count).Select(i => new IOException($"child {i}")).ToArray();
        parent = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var remaining = Count;
            var fibers = new Fiber<int, Never, EmptyEnvironment>[Count];
            for (var i = 0; i < Count; i++)
            {
                var failure = failures[i];
                var release = Effect<EmptyEnvironment>.Sync<Unit>(() => throw failure);
                var child = Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value), (_, _) => release)
                    .Use(_ => Effect<EmptyEnvironment>.Async<int>(async _ =>
                    {
                        if (Interlocked.Decrement(ref remaining) == 0) ready.SetResult();
                        await go.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        return 42;
                    }));
#pragma warning disable MEFF305
                fibers[i] = await scope.Fork(child);
#pragma warning restore MEFF305
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            go.SetResult();
            foreach (var fiber in fibers)
                await fiber.Join().RunExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return 42;
        });
        if (!Run().AsTask().GetAwaiter().GetResult().TryGetValue(out Die? die)
            || !failures.Contains(die.Exception) || die.Suppressed.Count != Count - 1 || die.OmittedSuppressed != 0)
        {
            throw new InvalidOperationException("Unexpected concurrent child failure.");
        }
    }

    [Benchmark] public ValueTask<Exit<int, Never>> Run() => parent.RunExitAsync();
}
