using System.Runtime.ExceptionServices;

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

    private static readonly object closed = new();

    private readonly CancellationTokenSource source;
    private int state;
    private ExceptionDispatchInfo? callbackFailure;
    private CancellationTokenRegistration callerLink;
    private object? callerFailure;

    internal Cancellation(CancellationToken parent) => source = CancellationTokenSource.CreateLinkedTokenSource(parent);

    /// <summary>
    /// A unit for the caller's token. It is not linked, but canceled from a registration, so a callback that throws when the caller
    /// cancels is kept for the run instead of being thrown from the caller's <c>Cancel()</c>.
    /// </summary>
    internal static Cancellation ForCaller(CancellationToken caller)
    {
        var unit = new Cancellation(CancellationToken.None);
        unit.callerLink = caller.UnsafeRegister(static self => ((Cancellation)self!).CancelForCaller(), unit);
        return unit;
    }

    /// <summary>Ends the run: puts a callback defect caught so far on the outcome; one caught later is reported instead.</summary>
    internal Outcome<T, E> CloseForCaller<T, E>(Outcome<T, E> outcome)
        => Interlocked.Exchange(ref callerFailure, closed) is ExceptionDispatchInfo failure
            ? outcome.After([new Suppressed(ExitCases.Die(failure), Relation.Consequence)])
            : outcome;

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
        catch (AggregateException exception)
        {
            // A callback registered on the token threw. The owner still has to wait for its work, so the defect is kept for it instead.
            callbackFailure = Capture(exception);
        }

        return true;
    }

    private void CancelForCaller()
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException exception)
        {
            // The run may already have ended inline on this thread; then no outcome is left to carry the defect.
            var failure = Capture(exception);
            if (Interlocked.CompareExchange(ref callerFailure, failure, null) == closed) UnobservedFailures.Report(ExitCases.Die(failure));
        }
    }

    // Linked tokens nest one AggregateException per level.
    private static ExceptionDispatchInfo Capture(AggregateException exception)
    {
        var flat = exception.Flatten();
        return ExceptionDispatchInfo.Capture(flat.InnerExceptions.Count == 1 ? flat.InnerExceptions[0] : flat);
    }

    /// <summary>The defect thrown by a callback on the token when this unit canceled it, if any.</summary>
    internal ExceptionDispatchInfo? CallbackFailure => Volatile.Read(ref callbackFailure);

    /// <summary>Puts the callback defect, if any, on the outcome as a failure that followed from the cancellation.</summary>
    internal Outcome<T, E> WithCallbackFailure<T, E>(Outcome<T, E> outcome)
        => CallbackFailure is { } failure ? outcome.After([new Suppressed(ExitCases.Die(failure), Relation.Consequence)]) : outcome;

    /// <summary>Marks the work as finished. Returns false if a reason won first.</summary>
    internal bool TrySettle() => Interlocked.CompareExchange(ref state, SETTLED, 0) == 0;

    public void Dispose()
    {
        callerLink.Dispose();
        source.Dispose();
    }
}
