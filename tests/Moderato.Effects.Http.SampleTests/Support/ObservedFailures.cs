using Moderato.Effects.Http;

public sealed class ObservedFailures
{
    public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
    public TaskCompletionSource<EffectHttpException> AfterResponseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisconnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisconnectCleaned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
