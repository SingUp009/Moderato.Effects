using System.Runtime.CompilerServices;
using Moderato.Effects;
using Moderato.Results;

internal static class RetentionProbe
{
    internal static int Run(int depth, bool pending)
    {
        // Given
        var effect = Effect<RunEnvironment>.AccessAsync<int>(static async (environment, _) =>
        {
            await environment.Completion.Task.ConfigureAwait(false);
            return environment.Value;
        });
        for (var i = 0; i < depth; i++) effect = effect.Map(static value => value + 1);
        var resource = Resource.Make(Effect<RunEnvironment>.AccessAsync<RunEnvironment>(static async (environment, _) =>
        {
            await environment.Completion.Task.ConfigureAwait(false);
            return environment;
        }), static (_, _) => Effect<RunEnvironment>.Succeed(Unit.Value)).Map(static environment => environment.Value);
        for (var i = 0; i < depth; i++) resource = resource.Map(static value => value + 1);
        var use = resource.Use(Effect<RunEnvironment>.Succeed);

        // When
        WeakReference[] references = [];
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                references = [RunOnce(effect, depth, pending), RunOnce(effect, depth, pending), RunOnce(use, depth, pending)];
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        { IsBackground = true };
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Retention worker did not finish.");
        if (failure is not null) throw new InvalidOperationException("Retention worker failed.", failure);

        // Then
        // Async scope unwinding can finish after delivering the public result.
        var collected = SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return references.All(static reference => !reference.IsAlive);
        }, TimeSpan.FromSeconds(10));
        if (!collected) throw new InvalidOperationException("A completed run retained its environment.");
        GC.KeepAlive(effect);
        GC.KeepAlive(use);
        Console.WriteLine($"PASS retention depth={depth} {(pending ? "pending" : "sync")}: completed environments collected while description stays alive");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunOnce(Effect<int, Never, RunEnvironment> effect, int depth, bool pending)
    {
        var environment = new RunEnvironment(3);
        var reference = new WeakReference(environment);
        if (!pending) environment.Completion.SetResult();
        var running = effect.RunAsync(environment, CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        if (pending) environment.Completion.SetResult();
        var result = running.AsTask().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (completed == pending || result != Result.Ok(3 + depth)) throw new InvalidOperationException("Retention probe changed execution behavior.");
        return reference;
    }

    private sealed class RunEnvironment(int value)
    {
        internal int Value { get; } = value;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
