namespace Moderato.Effects.Tracking.Samples.Execution;

public sealed class TrackingSession : IDisposable
{
    public const int CAPACITY = 2048;
    private readonly EffectRecording recording = EffectTracker.Record(CAPACITY);

    public EffectRecordingSnapshot GetSnapshot() => recording.GetSnapshot();
    public void Dispose() => recording.Dispose();
}
