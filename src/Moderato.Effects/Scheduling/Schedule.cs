using Moderato.Effects.Internal;

namespace Moderato.Effects;

/// <summary>When and how often <c>Retry</c> and <c>Repeat</c> try again, regardless of errors or values; without <see cref="UpTo"/> or <see cref="During"/> it has no limit.</summary>
public sealed class Schedule
{
    internal Schedule(ScheduleSpec spec) => Spec = spec;

    internal ScheduleSpec Spec { get; }

    /// <summary>Tries again without waiting.</summary>
    public static Schedule Immediately { get; } = new(new ScheduleSpec(TimeSpan.Zero, 1));

    /// <summary>Waits the same time before each new try.</summary>
    /// <param name="delay">The wait.</param>
    /// <returns>The schedule.</returns>
    public static Schedule Spaced(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        return new(new ScheduleSpec(delay, 1));
    }

    /// <summary>Waits <paramref name="baseDelay"/>, then multiplies the wait by <paramref name="factor"/> each time.</summary>
    /// <param name="baseDelay">The first wait.</param>
    /// <param name="factor">How much each wait grows, ≥ 1.</param>
    /// <returns>The schedule.</returns>
    public static Schedule Exponential(TimeSpan baseDelay, double factor = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        if (!double.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be finite.");
        return new(new ScheduleSpec(baseDelay, factor));
    }

    /// <summary>Tries again at most <paramref name="times"/> times: for Retry, up to <paramref name="times"/> + 1 attempts in all.</summary>
    /// <param name="times">The limit.</param>
    /// <returns>The limited schedule.</returns>
    public Schedule UpTo(int times) => new(Spec.UpTo(times));

    /// <summary>Stops once <paramref name="total"/> has passed since the first attempt, checked before each wait, so it is not a strict limit.</summary>
    /// <param name="total">The limit.</param>
    /// <returns>The limited schedule.</returns>
    public Schedule During(TimeSpan total) => new(Spec.During(total));

    /// <summary>Never waits longer than <paramref name="max"/>.</summary>
    /// <param name="max">The longest wait.</param>
    /// <returns>The capped schedule.</returns>
    public Schedule WithMaxDelay(TimeSpan max) => new(Spec.WithMaxDelay(max));

    /// <summary>Spreads each wait randomly over 80–120% so clients do not retry in step; needs <c>IHas&lt;Random&gt;</c>.</summary>
    /// <returns>A schedule that needs a random number generator from the environment.</returns>
    public Schedule<IHas<Random>> Jittered() => new(Spec with { Jittered = true });
}

/// <summary>A <see cref="Schedule"/> needing <typeparamref name="TNeed"/>, not derived from it so the need cannot be dropped.</summary>
/// <typeparam name="TNeed">What the environment must provide.</typeparam>
public sealed class Schedule<TNeed>
{
    internal Schedule(ScheduleSpec spec) => Spec = spec;

    internal ScheduleSpec Spec { get; }

    /// <inheritdoc cref="Schedule.UpTo"/>
    public Schedule<TNeed> UpTo(int times) => new(Spec.UpTo(times));

    /// <inheritdoc cref="Schedule.During"/>
    public Schedule<TNeed> During(TimeSpan total) => new(Spec.During(total));

    /// <inheritdoc cref="Schedule.WithMaxDelay"/>
    public Schedule<TNeed> WithMaxDelay(TimeSpan max) => new(Spec.WithMaxDelay(max));
}

/// <summary>The data behind both schedule types.</summary>
internal sealed record ScheduleSpec(TimeSpan BaseDelay, double Factor)
{
    internal int? MaxRecurrences { get; init; }

    internal TimeSpan? MaxElapsed { get; init; }

    internal TimeSpan? MaxDelay { get; init; }

    internal bool Jittered { get; init; }

    internal ScheduleSpec UpTo(int times)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(times);
        return this with { MaxRecurrences = times };
    }

    internal ScheduleSpec During(TimeSpan total)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(total, TimeSpan.Zero);
        return this with { MaxElapsed = total };
    }

    internal ScheduleSpec WithMaxDelay(TimeSpan max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, TimeSpan.Zero);
        return this with { MaxDelay = max };
    }

    /// <summary>The wait before recurrence number <paramref name="recurrence"/> (starting at 1), or <see langword="null"/> to stop.</summary>
    internal TimeSpan? Next(int recurrence, TimeSpan elapsed, Random? random)
    {
        if (recurrence > MaxRecurrences) return null;
        if (elapsed >= MaxElapsed) return null;

        // 0 × ∞ = NaN once Pow overflows, so a zero base is kept at zero explicitly.
        var ticks = BaseDelay == TimeSpan.Zero ? 0 : BaseDelay.Ticks * Math.Pow(Factor, recurrence - 1);
        if (Jittered && random is not null) ticks *= 0.8 + (random.NextDouble() * 0.4);
        if (MaxDelay is { } max) ticks = Math.Min(ticks, max.Ticks);
        // A growing schedule caps at the longest wait the timer can hold.
        return TimeSpan.FromTicks((long)Math.Min(ticks, Timeouts.LongestWait.Ticks));
    }
}
