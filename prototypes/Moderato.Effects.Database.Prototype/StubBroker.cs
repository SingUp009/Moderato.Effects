namespace Moderato.Effects.Database.Prototype;

public sealed class StubBroker
{
    public List<Message> Deliveries { get; } = [];
    public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool PauseAfterAccept { get; set; }

    public async ValueTask SendAsync(Message message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Deliveries.Add(message);
        Accepted.TrySetResult();
        if (PauseAfterAccept) await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
