using System.Diagnostics.CodeAnalysis;
using Moderato.Effects.Internal;

namespace Moderato.Effects;

/// <summary>What a retry policy sees after a failed attempt.</summary>
/// <typeparam name="E">The error.</typeparam>
/// <param name="Attempt">How many attempts have run so far, counting the one that just failed.</param>
/// <param name="Error">The error of the attempt that just failed.</param>
public readonly record struct RetryContext<E>(int Attempt, E Error)
{
    /// <summary>Writes the policy as a Gen block. A policy cannot fail, so the block is <c>Scope&lt;Never, R&gt;</c>.</summary>
    /// <param name="body">Returns whether to try again, after waiting as long as it likes.</param>
    /// <returns>The policy.</returns>
    public Effect<bool, Never, R> Gen<R>(Func<Scope<Never, R>, ValueTask<bool>> body) => Scope<Never, R>.Gen(body);
}

/// <summary>What a repeat policy sees after a successful run.</summary>
/// <typeparam name="T">The value.</typeparam>
/// <param name="Repetition">How many times the effect has run so far.</param>
/// <param name="Value">The value of the run that just succeeded.</param>
public readonly record struct RepeatContext<T>(int Repetition, T Value)
{
    /// <summary>Writes the policy as a Gen block. A policy cannot fail, so the block is <c>Scope&lt;Never, R&gt;</c>.</summary>
    /// <param name="body">Returns whether to run again, after waiting as long as it likes.</param>
    /// <returns>The policy.</returns>
    public Effect<bool, Never, R> Gen<R>(Func<Scope<Never, R>, ValueTask<bool>> body) => Scope<Never, R>.Gen(body);
}

/// <summary>
/// Retrying on Err and repeating on Ok. Each attempt runs the effect from the start, so its resources are released per attempt.
/// Defects and interruptions are never retried.
/// </summary>
public static class EffectScheduleExtensions
{
    private const string WhenJustification = "Overloads differ by schedule type; the 'when' filter is optional on each.";

    /// <summary>Tries again on Err, as the schedule says. When the tries run out, the last Err is the outcome.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to try again.</param>
    /// <param name="when">Which errors to retry. All of them by default.</param>
    /// <returns>An effect with the same types.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WhenJustification)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WhenJustification)]
    public static Effect<T, E, R> Retry<T, E, R>(this Effect<T, E, R> effect, Schedule schedule, Func<E, bool>? when = null)
        where R : IHas<TimeProvider>
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Retry(effect, ScheduledRetry<E, R>(schedule.Spec, when));
    }

    /// <summary>Tries again on Err, as the schedule says, with what the schedule needs from the environment.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to try again.</param>
    /// <param name="when">Which errors to retry. All of them by default.</param>
    /// <returns>An effect with the same types.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WhenJustification)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WhenJustification)]
    public static Effect<T, E, R> Retry<T, E, R, TNeed>(this Effect<T, E, R> effect, Schedule<TNeed> schedule, Func<E, bool>? when = null)
        where R : IHas<TimeProvider>, TNeed
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Retry(effect, ScheduledRetry<E, R>(schedule.Spec, when));
    }

    /// <summary>Tries again on Err for as long as the policy returns <see langword="true"/>. The policy does its own waiting.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="policy">Decides, after each failed attempt, whether to try again.</param>
    /// <returns>An effect with the same types.</returns>
    public static Effect<T, E, R> Retry<T, E, R>(this Effect<T, E, R> effect, Func<RetryContext<E>, Effect<bool, Never, R>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Recurrence.Retry(effect, (_, _) => policy);
    }

    /// <summary>Runs again on Ok, as the schedule says. An Err stops it; otherwise the last value is the outcome.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to run again.</param>
    /// <param name="when">Which values to run again after. All of them by default.</param>
    /// <returns>An effect with the last value.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WhenJustification)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WhenJustification)]
    public static Effect<T, E, R> Repeat<T, E, R>(this Effect<T, E, R> effect, Schedule schedule, Func<T, bool>? when = null)
        where R : IHas<TimeProvider>
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Repeat(effect, ScheduledRepeat<T, R>(schedule.Spec, when));
    }

    /// <summary>Runs again on Ok, as the schedule says, with what the schedule needs from the environment.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to run again.</param>
    /// <param name="when">Which values to run again after. All of them by default.</param>
    /// <returns>An effect with the last value.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WhenJustification)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WhenJustification)]
    public static Effect<T, E, R> Repeat<T, E, R, TNeed>(this Effect<T, E, R> effect, Schedule<TNeed> schedule, Func<T, bool>? when = null)
        where R : IHas<TimeProvider>, TNeed
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Repeat(effect, ScheduledRepeat<T, R>(schedule.Spec, when));
    }

    /// <summary>Runs again on Ok for as long as the policy returns <see langword="true"/>. The policy does its own waiting.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="policy">Decides, after each successful run, whether to run again.</param>
    /// <returns>An effect with the last value.</returns>
    public static Effect<T, E, R> Repeat<T, E, R>(this Effect<T, E, R> effect, Func<RepeatContext<T>, Effect<bool, Never, R>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Recurrence.Repeat(effect, (_, _) => policy);
    }

    // The schedule becomes a policy, so both forms share one executor. State is per run: a rerun starts its schedule over.
    private static Func<R, RunContext, Func<RetryContext<E>, Effect<bool, Never, R>>> ScheduledRetry<E, R>(ScheduleSpec spec, Func<E, bool>? when)
        where R : IHas<TimeProvider>
        => (env, _) =>
        {
            var clock = new ScheduleClock<R>(spec, env);
            return context => when is not null && !when(context.Error) ? Stop<R>() : clock.WaitBefore(context.Attempt);
        };

    private static Func<R, RunContext, Func<RepeatContext<T>, Effect<bool, Never, R>>> ScheduledRepeat<T, R>(ScheduleSpec spec, Func<T, bool>? when)
        where R : IHas<TimeProvider>
        => (env, _) =>
        {
            var clock = new ScheduleClock<R>(spec, env);
            return context => when is not null && !when(context.Value) ? Stop<R>() : clock.WaitBefore(context.Repetition);
        };

    private static Effect<bool, Never, R> Stop<R>() => Effect<R>.Succeed(false);
}
