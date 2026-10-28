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

/// <summary>A cancellation unit linked to its parent, where settling or the first reason wins for good.</summary>
internal class Cancellation : CancellationTokenSource
{
    internal sealed class CallbackFailure(ExceptionDispatchInfo dispatch, long order)
    {
        internal ExceptionDispatchInfo Dispatch { get; } = dispatch;
        internal long Order { get; } = order;
    }

    private const int SETTLED = -1;

    private static readonly object closed = new();

    private int state;
    private CancellationTokenRegistration parentLink;
    private object? callbackFailure;

    internal Cancellation(CancellationToken parent)
        => parentLink = parent.UnsafeRegister(static self => ((Cancellation)self!).CancelFromParent(), this);

    /// <summary>A unit for the caller's token, canceled from a registration so a throwing callback stays out of the caller's <c>Cancel()</c>.</summary>
    internal static Cancellation ForCaller(CancellationToken caller)
    {
        var unit = new Cancellation(CancellationToken.None);
        unit.parentLink = caller.UnsafeRegister(static self => ((Cancellation)self!).CancelForCaller(), unit);
        return unit;
    }

    /// <summary>The reason that won, or <see cref="CancellationReason.None"/> if the work settled first.</summary>
    internal CancellationReason Reason => Volatile.Read(ref state) is var current and > 0 ? (CancellationReason)current : CancellationReason.None;

    /// <summary>Cancels the work unless it already settled or another reason won.</summary>
    internal bool TryCancel(CancellationReason reason)
    {
        if (!TryBeginCancel(reason)) return false;
        CancelWork();
        return true;
    }

    protected bool TryBeginCancel(CancellationReason reason) => Interlocked.CompareExchange(ref state, (int)reason, 0) == 0;

    protected void CancelWork()
    {
        try
        {
            Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException exception)
        {
            // The owner still waits for its work, so a callback's defect is kept for it.
            Keep(exception);
        }
    }

    // Disposal must not turn parent cancellation into a callback failure.
    private void CancelFromParent()
    {
        try
        {
            Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelForCaller()
    {
        try
        {
            Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException exception)
        {
            Keep(exception);
        }
    }

    // A failure arriving after the owner took it has no outcome left to carry it, so it is reported.
    private void Keep(AggregateException exception)
    {
        var order = FailureSequence.Next();
        // Linked tokens nest one AggregateException per level.
        Exception single = exception;
        while (single is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            single = aggregate.InnerExceptions[0];
        if (single is AggregateException)
        {
            // Flatten the original aggregate to preserve its message.
            var flat = exception.Flatten();
            single = flat.InnerExceptions.Count == 1 ? flat.InnerExceptions[0] : flat;
        }

        var failure = new CallbackFailure(ExceptionDispatchInfo.Capture(single), order);
        if (Interlocked.CompareExchange(ref callbackFailure, failure, null) == closed) UnobservedFailures.Report(ExitCases.Die(failure.Dispatch));
    }

    /// <summary>Takes a callback's defect from the cancellation, after which defects are reported.</summary>
    internal CallbackFailure? TakeCallbackFailure() => Interlocked.Exchange(ref callbackFailure, closed) as CallbackFailure;

    /// <summary>Takes the callback defect, if any, onto the outcome as a consequence of the cancellation.</summary>
    internal Outcome<T, E> WithCallbackFailure<T, E>(Outcome<T, E> outcome)
    {
        if (TakeCallbackFailure() is not { } failure) return outcome;
        var retained = Math.Min(outcome.Suppressed.Length, SuppressedLimit.MAX - 1);
        return outcome.WithSuppressed([new Suppressed(ExitCases.Die(failure.Dispatch), Relation.Consequence), .. outcome.Suppressed.AsSpan()[..retained]],
            outcome.Omitted + outcome.Suppressed.Length - retained);
    }

    /// <summary>Marks the work finished; false if a reason won first.</summary>
    internal bool TrySettle() => Interlocked.CompareExchange(ref state, SETTLED, 0) == 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing) parentLink.Dispose();
        base.Dispose(disposing);
    }
}
