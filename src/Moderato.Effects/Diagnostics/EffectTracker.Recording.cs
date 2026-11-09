namespace Moderato.Effects;

public static partial class EffectTracker
{
    private static EffectRecording? recording;

    internal static EffectRecording? Recording => Volatile.Read(ref recording);

    /// <summary>Starts bounded execution recording independently of the active-lifetime snapshot.</summary>
    public static EffectRecording Record(int capacity = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var next = new EffectRecording(capacity);
        if (Interlocked.CompareExchange(ref recording, next, null) is not null)
            throw new InvalidOperationException("An execution recording is already active.");
        return next;
    }

    internal static void StopRecording(EffectRecording current)
        => Interlocked.CompareExchange(ref recording, null, current);
}
