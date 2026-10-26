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
    private const int SETTLED = -1;

    private readonly CancellationTokenSource source;
    private int state;

    internal Cancellation(CancellationToken parent) => source = CancellationTokenSource.CreateLinkedTokenSource(parent);

    /// <summary>The token for the owned work.</summary>
    internal CancellationToken Token => source.Token;

    /// <summary>The reason that won, or <see cref="CancellationReason.None"/> if the work settled first.</summary>
    internal CancellationReason Reason => Volatile.Read(ref state) is var current and > 0 ? (CancellationReason)current : CancellationReason.None;

    /// <summary>Cancels the work unless it already settled or another reason won.</summary>
    internal bool TryCancel(CancellationReason reason)
    {
        if (Interlocked.CompareExchange(ref state, (int)reason, 0) != 0) return false;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    /// <summary>Marks the work as finished. Returns false if a reason won first.</summary>
    internal bool TrySettle() => Interlocked.CompareExchange(ref state, SETTLED, 0) == 0;

    public void Dispose() => source.Dispose();
}
