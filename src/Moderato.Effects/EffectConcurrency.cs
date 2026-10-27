using System.Diagnostics.CodeAnalysis;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public static partial class Effect
{
    private const string CONCURRENCY_JUSTIFICATION = "Overloads differ by arity; concurrency defaults to Sequential so services that are not thread-safe stay safe.";

    /// <summary>Runs the effects and collects their values; the first failure cancels the rest and is the outcome unless an independent defect outranks it.</summary>
    /// <param name="effects">The effects.</param>
    /// <param name="concurrency">How they run; sequential by default, which keeps non-thread-safe services safe.</param>
    /// <returns>An effect whose value holds every value, in input order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<T[], E, R> All<T, E, R>(IEnumerable<Effect<T, E, R>> effects, Concurrency concurrency = default)
    {
        var runs = Runs(effects);
        return new((env, context) => RunGroup<T, E, R, T[], E>(runs, env, context, concurrency, StopOn.Failure, static (ends, context) =>
        {
            if (Concurrent.PrimaryFailure(ends, context.CancellationToken.IsCancellationRequested) is { } primary)
                return Concurrent.Fail<T, E, T[], E>(ends, primary, static error => error);
            return Succeeded<T, E, T[], E>(ends, static values => values);
        }));
    }

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2), E, R> All<T1, T2, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!));

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3), E, R> All<T1, T2, T3, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!));

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4), E, R> All<T1, T2, T3, T4, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!));

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5), E, R> All<T1, T2, T3, T4, T5, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!));

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="effect6">Effect 6.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5, T6), E, R> All<T1, T2, T3, T4, T5, T6, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Effect<T6, E, R> effect6, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5), Box(effect6)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!, (T6)values[5]!));

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="effect6">Effect 6.</param>
    /// <param name="effect7">Effect 7.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5, T6, T7), E, R> All<T1, T2, T3, T4, T5, T6, T7, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Effect<T6, E, R> effect6, Effect<T7, E, R> effect7, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5), Box(effect6), Box(effect7)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!, (T6)values[5]!, (T7)values[6]!));

    /// <summary>Runs the effects and pairs their values, like <see cref="All{T, E, R}(IEnumerable{Effect{T, E, R}}, Concurrency)"/>.</summary>
    /// <param name="effect1">Effect 1.</param>
    /// <param name="effect2">Effect 2.</param>
    /// <param name="effect3">Effect 3.</param>
    /// <param name="effect4">Effect 4.</param>
    /// <param name="effect5">Effect 5.</param>
    /// <param name="effect6">Effect 6.</param>
    /// <param name="effect7">Effect 7.</param>
    /// <param name="effect8">Effect 8.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds every value, in argument order.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = CONCURRENCY_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = CONCURRENCY_JUSTIFICATION)]
    public static Effect<(T1, T2, T3, T4, T5, T6, T7, T8), E, R> All<T1, T2, T3, T4, T5, T6, T7, T8, E, R>(Effect<T1, E, R> effect1, Effect<T2, E, R> effect2, Effect<T3, E, R> effect3, Effect<T4, E, R> effect4, Effect<T5, E, R> effect5, Effect<T6, E, R> effect6, Effect<T7, E, R> effect7, Effect<T8, E, R> effect8, Concurrency concurrency = default)
        => All([Box(effect1), Box(effect2), Box(effect3), Box(effect4), Box(effect5), Box(effect6), Box(effect7), Box(effect8)], concurrency).Map(static values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!, (T4)values[3]!, (T5)values[4]!, (T6)values[5]!, (T7)values[6]!, (T8)values[7]!));

    /// <summary>Runs every effect to the end and collects how each ended, an Err made from the caller's cancellation included.</summary>
    /// <param name="effects">The effects.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>An effect whose value holds each effect's exit, in input order.</returns>
    public static Effect<Exit<T, E>[], Never, R> AllSettled<T, E, R>(IEnumerable<Effect<T, E, R>> effects, Concurrency concurrency = default)
    {
        var runs = Runs(effects);
        return new((env, context) => RunGroup<T, E, R, Exit<T, E>[], Never>(runs, env, context, concurrency, StopOn.Nothing, static (ends, context) =>
        {
            if (Interrupted(ends, context)) return InterruptedWithOthers<T, E, Exit<T, E>[], Never>(ends);
            Exit<T, E>[] exits = ends.Length == 0 ? [] : new Exit<T, E>[ends.Length];
            for (var i = 0; i < ends.Length; i++) exits[i] = Exits.From(ends[i].Outcome);
            return Outcome<Exit<T, E>[], Never>.Ok(exits);
        }));
    }

    /// <summary>Runs every effect and collects all errors, as validation needs; a defect still cancels the rest.</summary>
    /// <param name="effects">The effects.</param>
    /// <param name="concurrency">How they run; sequential by default.</param>
    /// <returns>Every value, or every error, in input order.</returns>
    public static Effect<T[], E[], R> AllErrors<T, E, R>(IEnumerable<Effect<T, E, R>> effects, Concurrency concurrency = default)
    {
        var runs = Runs(effects);
        return new((env, context) => RunGroup<T, E, R, T[], E[]>(runs, env, context, concurrency, StopOn.Defect, static (ends, context) =>
        {
            if (Concurrent.PrimaryFailure(ends, context.CancellationToken.IsCancellationRequested) is { } primary
                && ends[primary].Outcome.Kind != OutcomeKind.Err)
            {
                return Concurrent.Fail<T, E, T[], E[]>(ends, primary, static error => [error]);
            }

            // Errors from a partial or canceled run would pass for a full validation, so they stay as context.
            if (Interrupted(ends, context) || CanceledIntoErr(ends, context)) return InterruptedWithOthers<T, E, T[], E[]>(ends);

            var errorCount = 0;
            foreach (var end in ends)
                if (end.Outcome.Kind == OutcomeKind.Err) errorCount++;
            if (errorCount > 0)
            {
                var (others, omitted) = Concurrent.Others(ends, null, includeErrs: false);
                var errors = new E[errorCount];
                var index = 0;
                foreach (var end in ends)
                    if (end.Outcome.Kind == OutcomeKind.Err) errors[index++] = end.Outcome.Error;
                return Outcome<T[], E[]>.Err(errors).WithSuppressed(others, omitted);
            }

            return Succeeded<T, E, T[], E[]>(ends, static values => values);
        }));
    }

    /// <summary>Takes the first effect to succeed and cancels the other; if both fail the first failure wins, and a defect ends the race.</summary>
    /// <param name="first">One effect.</param>
    /// <param name="second">The other effect.</param>
    /// <returns>An effect with the winner's value.</returns>
    public static Effect<T, E, R> Race<T, E, R>(Effect<T, E, R> first, Effect<T, E, R> second)
    {
        Effect<T, E, R>[] runs = [first.Validate(), second.Validate()];
        return new((env, context) => RunGroup<T, E, R, T, E>(runs, env, context, Concurrency.Parallel, StopOn.SuccessOrDefect, static (ends, context) =>
        {
            var winner = Earliest(ends, static end => end.Outcome.Kind == OutcomeKind.Ok && !end.AfterCancel);
            return Settle(ends, winner, context);
        }));
    }

    /// <summary>Takes the first effect to finish, Ok or not, and cancels the other; a loser's defect still outranks it.</summary>
    /// <param name="first">One effect.</param>
    /// <param name="second">The other effect.</param>
    /// <returns>An effect with the outcome of the first to finish.</returns>
    public static Effect<T, E, R> RaceFirst<T, E, R>(Effect<T, E, R> first, Effect<T, E, R> second)
    {
        Effect<T, E, R>[] runs = [first.Validate(), second.Validate()];
        return new((env, context) => RunGroup<T, E, R, T, E>(runs, env, context, Concurrency.Parallel, StopOn.Anything, static (ends, context) =>
        {
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

    // G3: an independent defect outranks the winner.
    private static Outcome<T, E> Settle<T, E>(ChildEnd<T, E>[] ends, int? winner, RunContext context)
    {
        var callerCanceled = context.CancellationToken.IsCancellationRequested;
        var primary = Concurrent.PrimaryFailure(ends, callerCanceled);
        // No winner ∧ no deciding failure ⇒ the caller canceled.
        if (primary is null && winner is null) return InterruptedWithOthers<T, E, T, E>(ends);
        var decidedByFailure = primary is { } p
            && (ends[p].Outcome.Kind == OutcomeKind.Die && ends[p].IsIndependentFailure
                || ends[p].Outcome.Kind == OutcomeKind.Interrupted && callerCanceled
                || winner is null);
        if (decidedByFailure) return Concurrent.Fail<T, E, T, E>(ends, primary!.Value, static error => error);

        var chosen = ends[winner!.Value].Outcome;
        var (others, omitted) = Concurrent.Others(ends, winner);
        return chosen.WithSuppressed(others, omitted).After(chosen);
    }

    private static Effect<T, E, R>[] Runs<T, E, R>(IEnumerable<Effect<T, E, R>> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        if (effects is not Effect<T, E, R>[] array) return [.. effects.Select(static effect => effect.Validate())];
        Effect<T, E, R>[] runs = array.Length == 0 ? [] : new Effect<T, E, R>[array.Length];
        for (var i = 0; i < array.Length; i++) runs[i] = array[i].Validate();
        return runs;
    }

    private static Effect<object?, E, R> Box<T, E, R>(Effect<T, E, R> effect) => effect.Map(static value => (object?)value);

    private static bool Interrupted<T, E>(ChildEnd<T, E>[] ends, RunContext context)
        => ends.Any(static end => !end.Ran || end.Outcome.Kind == OutcomeKind.Interrupted) && context.CancellationToken.IsCancellationRequested;

    private static bool CanceledIntoErr<T, E>(ChildEnd<T, E>[] ends, RunContext context)
        => context.CancellationToken.IsCancellationRequested && ends.Any(static end => end.Outcome.Kind == OutcomeKind.Err && end.AfterCancel);

    // The caller's cancellation decides, and the children's failures stay as context.
    private static Outcome<U, F> InterruptedWithOthers<T, E, U, F>(ChildEnd<T, E>[] ends)
    {
        var (others, omitted) = Concurrent.Others(ends, null);
        return Outcome<U, F>.Interrupted().WithSuppressed(others, omitted);
    }

    // No failure decided, so a child that is not Ok was stopped by the caller's cancellation.
    private static Outcome<U, F> Succeeded<T, E, U, F>(ChildEnd<T, E>[] ends, Func<T[], U> build)
    {
        if (ends.Any(static end => end.Outcome.Kind != OutcomeKind.Ok || !end.Ran)) return InterruptedWithOthers<T, E, U, F>(ends);
        var (others, omitted) = Concurrent.Others(ends, null);
        T[] values = ends.Length == 0 ? [] : new T[ends.Length];
        for (var i = 0; i < ends.Length; i++) values[i] = ends[i].Outcome.Value;
        return Outcome<U, F>.Ok(build(values)).WithSuppressed(others, omitted);
    }

    private static async ValueTask<Outcome<U, F>> RunGroup<T, E, R, U, F>(
        Effect<T, E, R>[] runs, R env, RunContext context, Concurrency concurrency, StopOn stopOn,
        Func<ChildEnd<T, E>[], RunContext, Outcome<U, F>> settle)
    {
        try
        {
            var ends = await Concurrent.RunAsync(runs, env, context, concurrency, stopOn).ConfigureAwait(false);
            return settle(ends, context);
        }
        catch (Exception exception)
        {
            return Defects.From<U, F>(exception, context);
        }
    }
}
