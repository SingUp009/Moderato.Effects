using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Which ending of a child cancels its siblings.</summary>
internal enum StopOn
{
    Nothing,
    Defect,
    Failure,
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
        OutcomeKind.Err => !AfterCancel,
        _ => false,
    };
}

/// <summary>Runs groups of effects under one cancellation unit and decides the group's outcome (D10, D18).</summary>
internal static class Concurrent
{
    /// <summary>Runs every child and waits for all that started, including their releases.</summary>
    internal static async ValueTask<ChildEnd<T, E>[]> RunAsync<T, E, R>(
        Func<R, RunContext, ValueTask<Outcome<T, E>>>[] children, R env, RunContext context, Concurrency concurrency, StopOn stopOn)
    {
        var ends = new ChildEnd<T, E>[children.Length];
        if (children.Length == 0) return ends;

        using var group = new Cancellation(context.CancellationToken);
        var groupContext = new RunContext(group.Token);
        var order = 0;

        if (!concurrency.IsParallel)
        {
            for (var i = 0; i < children.Length && !group.Token.IsCancellationRequested; i++)
            {
                await RunChildAsync(i).ConfigureAwait(false);
            }

            return ends;
        }

        var next = -1;
        var workers = new Task[Math.Min(concurrency.MaxDegree ?? children.Length, children.Length)];
        for (var w = 0; w < workers.Length; w++)
        {
            // On the thread pool so a child that blocks before its first await cannot keep its siblings from starting.
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

        async ValueTask RunChildAsync(int i)
        {
            Outcome<T, E> outcome;
            try
            {
                outcome = await children[i](env, groupContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outcome = Defects.From<T, E>(exception, groupContext);
            }

            var afterCancel = group.Token.IsCancellationRequested;
            ends[i] = new(outcome, afterCancel, Interlocked.Increment(ref order));
            if (Stops(outcome.Kind, stopOn)) group.TryCancel(CancellationReason.SiblingFailure);
        }
    }

    private static bool Stops(OutcomeKind kind, StopOn stopOn) => stopOn switch
    {
        StopOn.Failure => kind is OutcomeKind.Err or OutcomeKind.Die,
        StopOn.Defect => kind == OutcomeKind.Die,
        _ => false,
    };

    /// <summary>
    /// The failure that decides the group, if any: an independent Die first, then the caller's cancellation,
    /// then the earliest independent Err; a Die that only followed from cancellation comes last (F26).
    /// </summary>
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
        => current is { } c && ends[c].Order < ends[candidate].Order ? c : candidate;

    /// <summary>The failures of every child but the primary, in the order they ended, followed by what each child had suppressed itself.</summary>
    internal static (ImmutableArray<Suppressed> Suppressed, int Omitted) Others<T, E>(ChildEnd<T, E>[] ends, int? primary, bool includeErrs = true)
    {
        var builder = ImmutableArray.CreateBuilder<Suppressed>();
        var omitted = 0;
        foreach (var i in Enumerable.Range(0, ends.Length).Where(i => ends[i].Ran).OrderBy(i => ends[i].Order))
        {
            var end = ends[i];
            if (i != primary && (end.Outcome.Kind == OutcomeKind.Die || (includeErrs && end.Outcome.Kind == OutcomeKind.Err)))
            {
                var relation = end.IsIndependentFailure ? Relation.Parallel : Relation.Consequence;
                var failure = end.Outcome.Kind == OutcomeKind.Die ? ExitCases.Die(end.Outcome.Defect!) : ExitCases.Failed(end.Outcome.Error);
                builder.Add(new Suppressed(failure, relation));
            }

            if (i != primary)
            {
                builder.AddRange(end.Outcome.Suppressed);
                omitted += end.Outcome.Omitted;
            }
        }

        return (builder.ToImmutable(), omitted);
    }

    /// <summary>The primary child's failure, carrying the other children's failures as suppressed.</summary>
    internal static Outcome<U, F> Fail<T, E, U, F>(ChildEnd<T, E>[] ends, int primary, Func<E, F> error)
    {
        var (others, omitted) = Others(ends, primary);
        var chosen = ends[primary].Outcome;
        var failure = chosen.Kind == OutcomeKind.Err ? Outcome<U, F>.Err(error(chosen.Error)) : chosen.WithTypes<U, F>();
        return failure.WithSuppressed(chosen.Suppressed.AddRange(others), chosen.Omitted + omitted);
    }
}
