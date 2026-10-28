using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Moderato.Effects.Internal;

/// <summary>Which ending of a child cancels its siblings.</summary>
internal enum StopOn
{
    Nothing,
    Defect,
    Failure,
    SuccessOrDefect,
    Anything,
}

/// <summary>How one child of a group ended.</summary>
/// <param name="Outcome">The child's outcome.</param>
/// <param name="AfterCancel">Whether the group had already been canceled when the child ended.</param>
/// <param name="Order">When it ended, starting at 1; 0 if it never ran.</param>
internal readonly record struct ChildEnd<T, E>(Outcome<T, E> Outcome, bool AfterCancel, int Order)
{
    internal bool Ran => Order > 0;

    /// <summary>A failure that did not follow from the group's cancellation.</summary>
    internal bool IsIndependentFailure => Outcome.Kind switch
    {
        OutcomeKind.Die => !Outcome.Consequential,
        OutcomeKind.Err => !Outcome.Consequential,
        _ => false,
    };
}

/// <summary>Runs groups of effects under one cancellation unit and decides the group's outcome (D10, D18).</summary>
internal static class Concurrent
{
    private const int MAX_STACK_CHILDREN = 256;

    /// <summary>Runs every child and waits for all that started, releases included.</summary>
    internal static ValueTask<ChildEnd<T, E>[]> RunAsync<T, E, R>(
        Effect<T, E, R>[] children, R env, RunContext context, Concurrency concurrency, StopOn stopOn)
        => children.Length == 0 ? new([])
            : concurrency.IsParallel ? RunParallelAsync(children, env, context, concurrency, stopOn)
            : RunSequentialAsync(children, env, context, stopOn);

    private static async ValueTask<ChildEnd<T, E>[]> RunSequentialAsync<T, E, R>(
        Effect<T, E, R>[] children, R env, RunContext context, StopOn stopOn)
    {
        var ends = new ChildEnd<T, E>[children.Length];
        using var group = new Cancellation(context.CancellationToken);
        var groupContext = new RunContext(group.Token);
        for (var i = 0; i < children.Length && !group.Token.IsCancellationRequested; i++)
        {
            Outcome<T, E> outcome;
            try
            {
                outcome = await children[i].Run(env, groupContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outcome = Defects.From<T, E>(exception, groupContext);
            }

            var afterCancel = CompleteChild(ref outcome, group, stopOn);
            ends[i] = new(outcome, afterCancel, i + 1);
        }
        return ends;
    }

    private static async ValueTask<ChildEnd<T, E>[]> RunParallelAsync<T, E, R>(
        Effect<T, E, R>[] children, R env, RunContext context, Concurrency concurrency, StopOn stopOn)
    {
        var ends = new ChildEnd<T, E>[children.Length];
        using var group = new Cancellation(context.CancellationToken);
        var groupContext = new RunContext(group.Token);
        var order = 0;
        var next = -1;
        var workers = new Task[Math.Min(concurrency.MaxDegree ?? children.Length, children.Length)];
        for (var w = 0; w < workers.Length; w++)
        {
            // On the thread pool, so a child blocking before its first await cannot stall its siblings.
            workers[w] = Task.Run(async () =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < children.Length && !group.Token.IsCancellationRequested)
                {
                    await RunChildAsync(i).ConfigureAwait(false);
                }
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return ends;

        // Each worker awaits this ValueTask exactly once before starting its next child.
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        async ValueTask RunChildAsync(int i)
        {
            Outcome<T, E> outcome;
            try
            {
                outcome = await children[i].Run(env, groupContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outcome = Defects.From<T, E>(exception, groupContext);
            }

            var afterCancel = CompleteChild(ref outcome, group, stopOn);
            ends[i] = new(outcome, afterCancel, Interlocked.Increment(ref order));
        }
    }

    private static bool CompleteChild<T, E>(ref Outcome<T, E> outcome, Cancellation group, StopOn stopOn)
    {
        var afterCancel = group.Token.IsCancellationRequested;
        var reason = stopOn is StopOn.SuccessOrDefect or StopOn.Anything ? CancellationReason.RaceLoser : CancellationReason.SiblingFailure;
        // The child that cancels the group carries any callback defect.
        if (Stops(outcome.Kind, stopOn) && group.TryCancel(reason)) outcome = group.WithCallbackFailure(outcome);
        return afterCancel;
    }

    private static bool Stops(OutcomeKind kind, StopOn stopOn) => stopOn switch
    {
        StopOn.Failure => kind is OutcomeKind.Err or OutcomeKind.Die,
        StopOn.Defect => kind == OutcomeKind.Die,
        StopOn.SuccessOrDefect => kind is OutcomeKind.Ok or OutcomeKind.Die,
        StopOn.Anything => true,
        _ => false,
    };

    /// <summary>The deciding failure: independent Die &gt; caller's cancellation &gt; earliest independent Err &gt; consequential Die (F26).</summary>
    internal static int? PrimaryFailure<T, E>(ChildEnd<T, E>[] ends, bool callerCanceled)
    {
        int? independentDie = null, independentErr = null, anyDie = null, interrupted = null;
        for (var i = 0; i < ends.Length; i++)
        {
            var end = ends[i];
            if (!end.Ran) continue;
            switch (end.Outcome.Kind)
            {
                case OutcomeKind.Die when end.IsIndependentFailure:
                    independentDie = Earlier(ends, independentDie, i);
                    break;
                case OutcomeKind.Die:
                    anyDie = Earlier(ends, anyDie, i);
                    break;
                case OutcomeKind.Err when end.IsIndependentFailure:
                    independentErr = Earlier(ends, independentErr, i);
                    break;
                case OutcomeKind.Interrupted:
                    interrupted = Earlier(ends, interrupted, i);
                    break;
            }
        }

        if (independentDie is not null) return independentDie;
        if (callerCanceled && interrupted is not null) return interrupted;
        return independentErr ?? anyDie ?? interrupted;
    }

    private static int Earlier<T, E>(ChildEnd<T, E>[] ends, int? current, int candidate)
        => current is { } c && (ends[candidate].Outcome.Kind == OutcomeKind.Interrupted
            ? ends[c].Order < ends[candidate].Order
            : FailureSequence.Before(ends[c].Outcome.FailureOrder, ends[candidate].Outcome.FailureOrder)) ? c : candidate;

    /// <summary>Retained child context and non-primary failures in completion order, plus omitted counts.</summary>
    internal static (ImmutableArray<Suppressed> Suppressed, int Omitted) Context<T, E>(ChildEnd<T, E>[] ends, int? primary, bool includeErrs = true)
    {
        var omitted = 0;
        for (var i = 0; i < ends.Length; i++)
        {
            var end = ends[i];
            if (!end.Ran || i == primary) continue;
            if (end.Outcome.Kind == OutcomeKind.Die
                || (includeErrs && end.Outcome.Kind == OutcomeKind.Err)
                || !end.Outcome.Suppressed.IsEmpty)
            {
                return CollectContext(ends, primary, includeErrs);
            }
            omitted += end.Outcome.Omitted;
        }
        return primary is { } selected
            ? (ends[selected].Outcome.Suppressed, omitted + ends[selected].Outcome.Omitted)
            : ([], omitted);
    }

    private static (ImmutableArray<Suppressed> Suppressed, int Omitted) CollectContext<T, E>(ChildEnd<T, E>[] ends, int? primary, bool includeErrs)
    {
        var builder = ImmutableArray.CreateBuilder<Suppressed>();
        var omitted = 0;
        Span<int> indices = ends.Length <= MAX_STACK_CHILDREN ? stackalloc int[ends.Length] : new int[ends.Length];
        var count = 0;
        // Completed children have unique, contiguous ranks; unstarted children have no rank.
        for (var i = 0; i < ends.Length; i++)
        {
            if (!ends[i].Ran) continue;
            indices[ends[i].Order - 1] = i;
            count++;
        }

        System.Diagnostics.Debug.Assert(count == ends.Max(static end => end.Order));

        for (var order = 0; order < count; order++)
        {
            var i = indices[order];
            var end = ends[i];
            if (i != primary && (end.Outcome.Kind == OutcomeKind.Die || (includeErrs && end.Outcome.Kind == OutcomeKind.Err)))
            {
                var relation = end.IsIndependentFailure ? Relation.Parallel : Relation.Consequence;
                if (builder.Count < SuppressedLimit.MAX)
                {
                    var failure = end.Outcome.Kind == OutcomeKind.Die ? ExitCases.Die(end.Outcome.Defect!) : ExitCases.Failed(end.Outcome.Error);
                    builder.Add(new Suppressed(failure, relation));
                }
                else
                {
                    // Project discarded errors too: union getters may run user code.
                    if (end.Outcome.Kind == OutcomeKind.Err) _ = ExitCases.Failed(end.Outcome.Error);
                    omitted++;
                }
            }

            var retained = Math.Min(end.Outcome.Suppressed.Length, SuppressedLimit.MAX - builder.Count);
            builder.AddRange(end.Outcome.Suppressed.AsSpan()[..retained]);
            omitted += end.Outcome.Omitted + end.Outcome.Suppressed.Length - retained;
        }

        return (builder.DrainToImmutable(), omitted);
    }

    /// <summary>The primary child's failure, with the others' failures suppressed.</summary>
    internal static Outcome<U, F> Fail<T, E, U, F>(ChildEnd<T, E>[] ends, int primary, Func<E, F> error)
    {
        var (suppressed, omitted) = Context(ends, primary);
        var chosen = ends[primary].Outcome;
        var failure = chosen.Kind == OutcomeKind.Err ? chosen.WithError<U, F>(error(chosen.Error)) : chosen.WithTypes<U, F>();
        return failure.WithSuppressed(suppressed, omitted);
    }
}
