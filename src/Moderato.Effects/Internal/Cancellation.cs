namespace Moderato.Effects.Internal;

/// <summary>Why a cancellation unit canceled its work.</summary>
internal enum CancellationReason
{
    None = 0,
    Caller,
    Timeout,
    RaceLoser,
    SiblingFailure,
    ParentScopeClosed,
    ChildDefect,
}

/// <summary>
/// A unit that can cancel the work it owns, linked to its parent's token. Either the work settles first or one reason
/// wins; whichever comes first is final.
/// </summary>
internal sealed class Cancellation : IDisposable
{
    private const int Settled = -1;

    private readonly CancellationTokenSource _source;
    private int _state;

    internal Cancellation(CancellationToken parent) => _source = CancellationTokenSource.CreateLinkedTokenSource(parent);

    /// <summary>The token for the owned work.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>The reason that won, or <see cref="CancellationReason.None"/> if the work settled first.</summary>
    internal CancellationReason Reason => Volatile.Read(ref _state) is var state and > 0 ? (CancellationReason)state : CancellationReason.None;

    /// <summary>Cancels the work unless it already settled or another reason won.</summary>
    internal bool TryCancel(CancellationReason reason)
    {
        if (Interlocked.CompareExchange(ref _state, (int)reason, 0) != 0) return false;
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    /// <summary>Marks the work as finished. Returns false if a reason won first.</summary>
    internal bool TrySettle() => Interlocked.CompareExchange(ref _state, Settled, 0) == 0;

    public void Dispose() => _source.Dispose();
}
