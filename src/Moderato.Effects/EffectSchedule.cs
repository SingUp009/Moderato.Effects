using System.Diagnostics.CodeAnalysis;
using Moderato.Effects.Internal;

namespace Moderato.Effects;

/// <summary>What a retry policy sees after a failed attempt.</summary>
/// <typeparam name="E">The error.</typeparam>
/// <param name="Attempt">How many attempts have run so far, counting the one that just failed.</param>
/// <param name="Error">The error of the attempt that just failed.</param>
public readonly record struct RetryContext<E>(int Attempt, E Error)
{
    /// <summary>Writes the policy as a Gen block, which cannot fail (<c>Scope&lt;Never, R&gt;</c>).</summary>
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
    /// <summary>Writes the policy as a Gen block, which cannot fail (<c>Scope&lt;Never, R&gt;</c>).</summary>
    /// <param name="body">Returns whether to run again, after waiting as long as it likes.</param>
    /// <returns>The policy.</returns>
    public Effect<bool, Never, R> Gen<R>(Func<Scope<Never, R>, ValueTask<bool>> body) => Scope<Never, R>.Gen(body);
}

/// <summary>Retry on Err and Repeat on Ok, rerunning the effect from the start; defects and interruptions are never retried.</summary>
public static class EffectScheduleExtensions
{
    private const string WHEN_JUSTIFICATION = "Overloads differ by schedule type; the 'when' filter is optional on each.";

    /// <summary>Tries again on Err as the schedule says, ending with the last Err.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to try again.</param>
    /// <param name="when">Which errors to retry; all by default.</param>
    /// <returns>An effect with the same types.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WHEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WHEN_JUSTIFICATION)]
    public static Effect<T, E, R> Retry<T, E, R>(this Effect<T, E, R> effect, Schedule schedule, Func<E, bool>? when = null)
        where R : IHas<TimeProvider>
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Retry(effect, (env, _) => new ScheduledRetry<E, R>(schedule.Spec, env, when));
    }

    /// <summary>Tries again on Err as the schedule says, with what it needs from the environment.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to try again.</param>
    /// <param name="when">Which errors to retry; all by default.</param>
    /// <returns>An effect with the same types.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WHEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WHEN_JUSTIFICATION)]
    public static Effect<T, E, R> Retry<T, E, R, TNeed>(this Effect<T, E, R> effect, Schedule<TNeed> schedule, Func<E, bool>? when = null)
        where R : IHas<TimeProvider>, TNeed
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Retry(effect, (env, _) => new ScheduledRetry<E, R>(schedule.Spec, env, when));
    }

    /// <summary>Tries again on Err while the policy, which does its own waiting, returns <see langword="true"/>.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="policy">Decides, after each failed attempt, whether to try again.</param>
    /// <returns>An effect with the same types.</returns>
    public static Effect<T, E, R> Retry<T, E, R>(this Effect<T, E, R> effect, Func<RetryContext<E>, Effect<bool, Never, R>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Recurrence.Retry(effect, (_, _) => new EffectPolicy<RetryContext<E>, R>(policy));
    }

    /// <summary>Runs again on Ok as the schedule says, ending with the last value or the first Err.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to run again.</param>
    /// <param name="when">Which values to run again after; all by default.</param>
    /// <returns>An effect with the last value.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WHEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WHEN_JUSTIFICATION)]
    public static Effect<T, E, R> Repeat<T, E, R>(this Effect<T, E, R> effect, Schedule schedule, Func<T, bool>? when = null)
        where R : IHas<TimeProvider>
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Repeat(effect, (env, _) => new ScheduledRepeat<T, R>(schedule.Spec, env, when));
    }

    /// <summary>Runs again on Ok as the schedule says, with what it needs from the environment.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="schedule">When and how many times to run again.</param>
    /// <param name="when">Which values to run again after; all by default.</param>
    /// <returns>An effect with the last value.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = WHEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = WHEN_JUSTIFICATION)]
    public static Effect<T, E, R> Repeat<T, E, R, TNeed>(this Effect<T, E, R> effect, Schedule<TNeed> schedule, Func<T, bool>? when = null)
        where R : IHas<TimeProvider>, TNeed
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Recurrence.Repeat(effect, (env, _) => new ScheduledRepeat<T, R>(schedule.Spec, env, when));
    }

    /// <summary>Runs again on Ok while the policy, which does its own waiting, returns <see langword="true"/>.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="policy">Decides, after each successful run, whether to run again.</param>
    /// <returns>An effect with the last value.</returns>
    public static Effect<T, E, R> Repeat<T, E, R>(this Effect<T, E, R> effect, Func<RepeatContext<T>, Effect<bool, Never, R>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Recurrence.Repeat(effect, (_, _) => new EffectPolicy<RepeatContext<T>, R>(policy));
    }
}
