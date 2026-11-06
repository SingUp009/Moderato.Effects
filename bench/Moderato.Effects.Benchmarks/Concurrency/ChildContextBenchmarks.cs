using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ChildContextBenchmarks
{
    [Params(0, 100)]
    public int ContextCount;

    private Effect<int, Never, EmptyEnvironment> parent;

    [GlobalSetup]
    public void Setup()
    {
        var firstFailure = new IOException("first child");
        var lastFailure = new IOException("last child");
        var cleanup = Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("cleanup"));
        parent = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = await scope.Fork(Child(firstStarted, firstGo, firstFailure));
            var last = await scope.Fork(Child(lastStarted, lastGo, lastFailure));
            await Task.WhenAll(firstStarted.Task, lastStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            firstGo.SetResult();
            await first.Join().RunExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            lastGo.SetResult();
            await last.Join().RunExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return 42;
        });

        if (!Run().AsTask().GetAwaiter().GetResult().TryGetValue(out Die? die)
            || !ReferenceEquals(die.Exception, firstFailure)
            || die.Suppressed.Count != Math.Min(100, 2 * ContextCount + 1)
            || die.OmittedSuppressed != Math.Max(0, 2 * ContextCount - 99))
        {
            throw new InvalidOperationException("Unexpected child context.");
        }

        Effect<int, Never, EmptyEnvironment> Child(TaskCompletionSource started, TaskCompletionSource go, Exception failure)
            => Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
            {
                for (var i = 0; i < ContextCount; i++)
                {
#pragma warning disable MEFF305
                    scope.Defer(_ => cleanup);
#pragma warning restore MEFF305
                }

                scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw failure));
                started.SetResult();
                await go.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return 42;
            });
    }

    [Benchmark] public ValueTask<Exit<int, Never>> Run() => parent.RunExitAsync();
}
