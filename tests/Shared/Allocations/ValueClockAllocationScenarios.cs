using Moderato.Results;

namespace Moderato.Effects.Testing;

public readonly struct ValueClock : IHas<TimeProvider>
{
    TimeProvider IHas<TimeProvider>.Service => TimeProvider.System;
}

public static class ValueClockAllocationScenarios
{
    public static (Effect<int, string, ValueClock> Effect, Result<int, string> Expected) Create(int operation)
    {
        Effect<int, string, ValueClock> source = Effect<ValueClock>.FromResult<int, string>(Result.Ok(20));
        Effect<int, string, ValueClock> failed = Effect<ValueClock>.Fail("failed");
        Effect<int, string, ValueClock> effect = operation switch
        {
            0 => Effect.Sleep<ValueClock>(TimeSpan.Zero).Map(static _ => 20),
            1 => failed.Retry(Schedule.Immediately.UpTo(7)),
            2 => source.Repeat(Schedule.Immediately.UpTo(7)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        Result<int, string> expected = operation == 1 ? Result.Err("failed") : Result.Ok(20);
        return (effect, expected);
    }
}
