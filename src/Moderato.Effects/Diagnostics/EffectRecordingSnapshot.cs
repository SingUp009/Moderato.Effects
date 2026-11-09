namespace Moderato.Effects;

/// <summary>A frozen view of retained executions started during one recording.</summary>
public sealed class EffectRecordingSnapshot
{
    internal EffectRecordingSnapshot(TimeSpan elapsed, IReadOnlyList<EffectRecordingEntry> entries, long dropped, long evicted)
    {
        Elapsed = elapsed;
        Entries = entries;
        Dropped = dropped;
        Evicted = evicted;
    }

    /// <summary>Elapsed real time since recording began.</summary>
    public TimeSpan Elapsed { get; }
    public IReadOnlyList<EffectRecordingEntry> Entries { get; }
    public long Dropped { get; }
    public long Evicted { get; }
    /// <summary>Whether no entries were dropped or evicted from this recording.</summary>
    public bool IsComplete => Dropped == 0 && Evicted == 0;
}
