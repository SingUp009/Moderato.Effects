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
internal sealed class Cancellation : IDisposable
{
    private const int SETTLED = -1;

    private static readonly object closed = new();

    private readonly CancellationTokenSource source;
    private int state;
    private CancellationTokenRegistration callerLink;
    private object? callbackFailure;

    internal Cancellation(CancellationToken parent) => source = CancellationTokenSource.CreateLinkedTokenSource(parent);

    /// <summary>A unit for the caller's token, canceled from a registration so a throwing callback stays out of the caller's <c>Cancel()</c>.</summary>
    internal static Cancellation ForCaller(CancellationToken caller)
    {
        var unit = new Cancellation(CancellationToken.None);
        unit.callerLink = caller.UnsafeRegister(static self => ((Cancellation)self!).CancelForCaller(), unit);
        return unit;
    }

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
            // The owner still waits for its work, so a callback's defect is kept for it.
            Keep(exception);
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
            Keep(exception);
        }
    }

    // A failure arriving after the owner took it has no outcome left to carry it, so it is reported.
    private void Keep(AggregateException exception)
    {
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

        var failure = ExceptionDispatchInfo.Capture(single);
        if (Interlocked.CompareExchange(ref callbackFailure, failure, null) == closed) UnobservedFailures.Report(ExitCases.Die(failure));
    }

    /// <summary>Takes a callback's defect from the cancellation, after which defects are reported.</summary>
    internal ExceptionDispatchInfo? TakeCallbackFailure() => Interlocked.Exchange(ref callbackFailure, closed) as ExceptionDispatchInfo;

    /// <summary>Takes the callback defect, if any, onto the outcome as a consequence of the cancellation.</summary>
    internal Outcome<T, E> WithCallbackFailure<T, E>(Outcome<T, E> outcome)
        => TakeCallbackFailure() is { } failure ? outcome.After([new Suppressed(ExitCases.Die(failure), Relation.Consequence)]) : outcome;

    /// <summary>Marks the work finished; false if a reason won first.</summary>
    internal bool TrySettle() => Interlocked.CompareExchange(ref state, SETTLED, 0) == 0;

    public void Dispose()
    {
        callerLink.Dispose();
        source.Dispose();
    }
}
