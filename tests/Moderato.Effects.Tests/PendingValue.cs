using System.Threading.Tasks.Sources;

namespace Moderato.Effects.Tests;

internal sealed class PendingValue<T>(bool runContinuationsAsynchronously = true) : IValueTaskSource<T>
{
    private ManualResetValueTaskSourceCore<T> core = new() { RunContinuationsAsynchronously = runContinuationsAsynchronously };
    private readonly TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ValueTask<T> Task => new(this, core.Version);
    internal Task Registered => registered.Task;
    internal void Complete(T value) => core.SetResult(value);
    public T GetResult(short token) => core.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        core.OnCompleted(continuation, state, token, flags);
        registered.SetResult();
    }
}
