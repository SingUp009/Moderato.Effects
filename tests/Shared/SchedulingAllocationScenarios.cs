using Moderato.Results;

namespace Moderato.Effects.Testing;

public enum SchedulingAllocationCase
{
    SleepZero, RetrySuccess, RetryStop, RetryEightAttempts, RetryOnce,
    RetryFilter, RepeatStop, RepeatEightRuns, RepeatFilter,
    SpacedZero, ExponentialZero, CappedToZero, DuringZero,
}

public static class SchedulingAllocationScenarios
{
    public static (Effect<int, string, EmptyEnvironment> Effect, Result<int, string> Expected) Create(SchedulingAllocationCase scenario)
    {
        var source = Effect<DefaultEnvironment>.FromResult<int, string>(Result.Ok(20));
        Effect<int, string, DefaultEnvironment> failed = Effect<DefaultEnvironment>.Fail("failed");
        var immediately = Schedule.Immediately.UpTo(7);
        Effect<int, string, DefaultEnvironment> effect;
        Result<int, string> expected = Result.Err("failed");

        switch (scenario)
        {
            case SchedulingAllocationCase.SleepZero:
                effect = Effect.Sleep<DefaultEnvironment>(TimeSpan.Zero).Map(static _ => 42);
                expected = Result.Ok(42);
                break;
            case SchedulingAllocationCase.RetrySuccess:
                effect = source.Retry(immediately);
                expected = Result.Ok(20);
                break;
            case SchedulingAllocationCase.RetryStop:
                effect = failed.Retry(Schedule.Immediately.UpTo(0));
                break;
            case SchedulingAllocationCase.RetryEightAttempts:
                effect = failed.Retry(immediately);
                break;
            case SchedulingAllocationCase.RetryOnce:
                var calls = 0;
                Effect<int, string, DefaultEnvironment> counted = Effect<DefaultEnvironment>.Sync(() => ++calls);
                effect = counted.AndThen(n => (n & 1) == 1 ? failed : source).Retry(immediately);
                expected = Result.Ok(20);
                break;
            case SchedulingAllocationCase.RetryFilter:
                effect = failed.Retry(immediately, static _ => false);
                break;
            case SchedulingAllocationCase.RepeatStop:
                effect = source.Repeat(Schedule.Immediately.UpTo(0));
                expected = Result.Ok(20);
                break;
            case SchedulingAllocationCase.RepeatEightRuns:
                effect = source.Repeat(immediately);
                expected = Result.Ok(20);
                break;
            case SchedulingAllocationCase.RepeatFilter:
                effect = source.Repeat(immediately, static _ => false);
                expected = Result.Ok(20);
                break;
            case SchedulingAllocationCase.SpacedZero:
                effect = failed.Retry(Schedule.Spaced(TimeSpan.Zero).UpTo(7));
                break;
            case SchedulingAllocationCase.ExponentialZero:
                effect = failed.Retry(Schedule.Exponential(TimeSpan.Zero).UpTo(7));
                break;
            case SchedulingAllocationCase.CappedToZero:
                effect = failed.Retry(Schedule.Spaced(TimeSpan.FromSeconds(1)).WithMaxDelay(TimeSpan.Zero).UpTo(7));
                break;
            case SchedulingAllocationCase.DuringZero:
                effect = failed.Retry(Schedule.Immediately.During(TimeSpan.Zero));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return (effect.Provide<EmptyEnvironment>(static _ => DefaultEnvironment.Value), expected);
    }
}
