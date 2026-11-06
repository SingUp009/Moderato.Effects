using Moderato.Effects;

public static class Remote
{
    private static int calls;

    // Fails its first call, so the retry has something to do.
    public static Effect<string, TimeoutError, DefaultEnvironment> Fetch(string key, TimeSpan latency) => Scope<TimeoutError, DefaultEnvironment>.Gen(async scope =>
    {
        await scope.Run(Effect.Sleep<DefaultEnvironment>(latency));
        if (Interlocked.Increment(ref calls) == 1) throw scope.Fail(new TimeoutError(latency));
        return $"{key}:{latency.TotalMilliseconds}ms";
    });
}
