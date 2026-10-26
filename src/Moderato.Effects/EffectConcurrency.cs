using System.Diagnostics.CodeAnalysis;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public static partial class Effect
{
    private const string CONCURRENCY_JUSTIFICATION = "Overloads differ by arity; concurrency defaults to Sequential so services that are not thread-safe stay safe.";

    /// <summary>
    /// Runs the effects and collects their values. The first failure cancels the rest and the result waits until they have finished,
    /// including their releases. The failure that started the cancellation is the outcome; an independent defect outranks it.
    /// </summary>
    /// <param name="effects">The effects.</param>
    /// <param name="concurrency">How they run. Sequential by default, so services that are not thread-safe stay safe.</param>
    /// <returns>An effect whose value holds every value, in input order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<T[], E, R> All<T, E, R>(IEnumerable<Effect<T, E, R>> effects, Concurrency concurrency = default)
    {
        var runs = Runs(effects);
        return new((env, context) => Guard(context, async () =>
        {
            var ends = await Concurrent.RunAsync(runs, env, context, concurrency, StopOn.Failure).ConfigureAwait(false);
            if (Concurrent.PrimaryFailure(ends, context.CancellationToken.IsCancellationRequested) is { } primary)
                return Concurrent.Fail<T, E, T[], E>(ends, primary, static error => error);
            return Succeeded<T, E, T[], E>(ends, static values => values);
        }));
    }

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2), E, R> All<T1, T2, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!));

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3), E, R> All<T1, T2, T3, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!));

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4), E, R> All<T1, T2, T3, T4, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!));

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5), E, R> All<T1, T2, T3, T4, T5, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!));

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="effect6">Effect 6.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5, T6), E, R> All<T1, T2, T3, T4, T5, T6, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Effect<T6, E, R> effect6, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5), Box(effect6)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!, (T6)values[5]!));

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="effect6">Effect 6.</param>
    /// <param name="effect7">Effect 7.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5, T6, T7), E, R> All<T1, T2, T3, T4, T5, T6, T7, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Effect<T6, E, R> effect6, Effect<T7, E, R> effect7, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5), Box(effect6), Box(effect7)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!, (T6)values[5]!, (T7)values[6]!));

    /// <summary>Runs the effects and pairs their values. The first failure cancels the rest; see <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="effect6">Effect 6.</param>
    /// <param name="effect7">Effect 7.</param>
    /// <param name="effect8">Effect 8.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5, T6, T7, T8), E, R> All<T1, T2, T3, T4, T5, T6, T7, T8, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Effect<T6, E, R> effect6, Effect<T7, E, R> effect7, Effect<T8, E, R> effect8, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5), Box(effect6), Box(effect7), Box(effect8)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!, (T6)values[5]!, (T7)values[6]!, (T8)values[7]!));

    /// <summary>Runs the effects to the end, whatever each one does, and collects how each ended.</summary>
    /// <param name="effects">The effects.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds each effect's exit, in input order.</returns>
    public static Effect<Exit<T, E>[], Never, R> AllSettled<T, E, R>(IEnumerable<Effect<T, E, R>> effects, Concurrency concurrency = default)
    {
        var runs = Runs(effects);
        return new((env, context) => Guard<Exit<T, E>[], Never>(context, async () =>
        {
            var ends = await Concurrent.RunAsync(runs, env, context, concurrency, StopOn.Nothing).ConfigureAwait(false);
            if (Interrupted(ends, context)) return InterruptedWithOthers<T, E, Exit<T, E>[], Never>(ends);
            return Outcome<Exit<T, E>[], Never>.Ok([.. ends.Select(static end => Exits.From(end.Outcome))]);
        }));
    }

    /// <summary>
    /// Runs every effect and collects all the errors instead of stopping at the first, as input validation needs.
    /// A defect still cancels the rest.
    /// </summary>
    /// <param name="effects">The effects.</param>
    /// <param name="concurrency">How they run. Sequential by default.</param>
    /// <returns>An effect whose value holds every value, or whose error holds every error, in input order.</returns>
    public static Effect<T[], E[], R> AllErrors<T, E, R>(IEnumerable<Effect<T, E, R>> effects, Concurrency concurrency = default)
    {
        var runs = Runs(effects);
        return new((env, context) => Guard<T[], E[]>(context, async () =>
        {
            var ends = await Concurrent.RunAsync(runs, env, context, concurrency, StopOn.Defect).ConfigureAwait(false);
            if (Concurrent.PrimaryFailure(ends, context.CancellationToken.IsCancellationRequested) is { } primary
                && ends[primary].Outcome.Kind != OutcomeKind.Err)
            {
                return Concurrent.Fail<T, E, T[], E[]>(ends, primary, static error => [error]);
            }

            // Errors from only the children that ran would look like a complete validation; they stay as context instead.
            if (Interrupted(ends, context)) return InterruptedWithOthers<T, E, T[], E[]>(ends);

            if (ends.Any(static end => end.Outcome.Kind == OutcomeKind.Err))
            {
                var (others, omitted) = Concurrent.Others(ends, null, includeErrs: false);
                E[] errors = [.. ends.Where(static end => end.Outcome.Kind == OutcomeKind.Err).Select(static end => end.Outcome.Error)];
                return Outcome<T[], E[]>.Err(errors).WithSuppressed(others, omitted);
            }

            return Succeeded<T, E, T[], E[]>(ends, static values => values);
        }));
    }

    /// <summary>
    /// Runs both effects at the same time and takes the first to succeed; the other is canceled and waited for. If one fails, the other
    /// still gets its chance; if both fail, the first failure is the outcome. A defect on either side ends the race with that defect.
    /// </summary>
    /// <param name="first">One effect.</param>
    /// <param name="second">The other effect.</param>
    /// <returns>An effect with the winner's value.</returns>
    public static Effect<T, E, R> Race<T, E, R>(Effect<T, E, R> first, Effect<T, E, R> second)
    {
        Func<R, RunContext, ValueTask<Outcome<T, E>>>[] runs = [first.Run, second.Run];
        return new((env, context) => Guard(context, async () =>
        {
            var ends = await Concurrent.RunAsync(runs, env, context, Concurrency.Parallel, StopOn.SuccessOrDefect).ConfigureAwait(false);
            var winner = Earliest(ends, static end => end.Outcome.Kind == OutcomeKind.Ok && !end.AfterCancel);
            return Settle(ends, winner, context);
        }));
    }

    /// <summary>
    /// Runs both effects at the same time and takes the first to finish, whether it succeeded or failed; the other is canceled
    /// and waited for. A defect on the losing side still outranks the winner.
    /// </summary>
    /// <param name="first">One effect.</param>
    /// <param name="second">The other effect.</param>
    /// <returns>An effect with the outcome of the first to finish.</returns>
    public static Effect<T, E, R> RaceFirst<T, E, R>(Effect<T, E, R> first, Effect<T, E, R> second)
    {
        Func<R, RunContext, ValueTask<Outcome<T, E>>>[] runs = [first.Run, second.Run];
        return new((env, context) => Guard(context, async () =>
        {
            var ends = await Concurrent.RunAsync(runs, env, context, Concurrency.Parallel, StopOn.Anything).ConfigureAwait(false);
            var winner = Earliest(ends, static end => end.Outcome.Kind is OutcomeKind.Ok or OutcomeKind.Err && !end.AfterCancel);
            return Settle(ends, winner, context);
        }));
    }

    private static int? Earliest<T, E>(ChildEnd<T, E>[] ends, Func<ChildEnd<T, E>, bool> match)
    {
        int? earliest = null;
        for (var i = 0; i < ends.Length; i++)
        {
            if (ends[i].Ran && match(ends[i]) && (earliest is not { } e || ends[i].Order < ends[e].Order)) earliest = i;
        }

        return earliest;
    }

    // An independent defect anywhere outranks the winner, so a loser's failing release is never dropped (G3).
    private static Outcome<T, E> Settle<T, E>(ChildEnd<T, E>[] ends, int? winner, RunContext context)
    {
        var callerCanceled = context.CancellationToken.IsCancellationRequested;
        var primary = Concurrent.PrimaryFailure(ends, callerCanceled);
        // Nothing finished: the caller canceled before either side could start.
        if (primary is null && winner is null) return Outcome<T, E>.Interrupted();
        var decidedByFailure = primary is { } p
            && (ends[p].Outcome.Kind == OutcomeKind.Die && ends[p].IsIndependentFailure
                || ends[p].Outcome.Kind == OutcomeKind.Interrupted && callerCanceled
                || winner is null);
        if (decidedByFailure) return Concurrent.Fail<T, E, T, E>(ends, primary!.Value, static error => error);

        var chosen = ends[winner!.Value].Outcome;
        var (others, omitted) = Concurrent.Others(ends, winner);
        return chosen.WithSuppressed(chosen.Suppressed.AddRange(others), chosen.Omitted + omitted);
    }

    private static Func<R, RunContext, ValueTask<Outcome<T, E>>>[] Runs<T, E, R>(IEnumerable<Effect<T, E, R>> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        return [.. effects.Select(static effect => effect.Run)];
    }

    private static Effect<object?, E, R> Box<T, E, R>(Effect<T, E, R> effect) => effect.Map(static value => (object?)value);

    private static bool Interrupted<T, E>(ChildEnd<T, E>[] ends, RunContext context)
        => ends.Any(static end => !end.Ran || end.Outcome.Kind == OutcomeKind.Interrupted) && context.CancellationToken.IsCancellationRequested;

    // Every child ran and none failed; a child that never ran means the caller canceled before it could start.
    // Cancellation decides the outcome, but what the children already failed with stays as context rather than vanishing.
    private static Outcome<U, F> InterruptedWithOthers<T, E, U, F>(ChildEnd<T, E>[] ends)
    {
        var (others, omitted) = Concurrent.Others(ends, null);
        return Outcome<U, F>.Interrupted().WithSuppressed(others, omitted);
    }

    private static Outcome<U, F> Succeeded<T, E, U, F>(ChildEnd<T, E>[] ends, Func<T[], U> build)
    {
        if (ends.Any(static end => !end.Ran)) return InterruptedWithOthers<T, E, U, F>(ends);
        var (others, omitted) = Concurrent.Others(ends, null);
        return Outcome<U, F>.Ok(build([.. ends.Select(static end => end.Outcome.Value)])).WithSuppressed(others, omitted);
    }

    private static async ValueTask<Outcome<T, E>> Guard<T, E>(RunContext context, Func<ValueTask<Outcome<T, E>>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context);
        }
    }
}
