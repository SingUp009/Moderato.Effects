using System.Diagnostics;

namespace Moderato.Effects;

/// <summary>Copied execution metadata; application objects and error values are not retained.</summary>
public sealed class EffectRecordingEntry
{
    internal EffectRecordingEntry(TrackingNode node, long origin, TimeSpan capturedAt)
    {
        Id = node.Id;
        ParentId = node.ParentId;
        Kind = node.Kind;
        Name = node.Name;
        State = node.State;
        Detail = node.Detail;
        BodyState = node.BodyState;
        BodyDetail = node.BodyDetail;
        OwnerId = node.OwnerId;
        AcquiredBy = node.AcquiredBy;
        Location = node.Location;
        TraceId = node.TraceId;
        SpanId = node.SpanId;
        StartOffset = Stopwatch.GetElapsedTime(origin, node.StartTimestamp);
        EndOffset = node.EndTimestamp is { } end ? Stopwatch.GetElapsedTime(origin, end) : null;
        Elapsed = (EndOffset ?? capturedAt) - StartOffset;
    }

    public long Id { get; }
    public long? ParentId { get; }
    public string Kind { get; }
    public string Name { get; }
    /// <summary>The boundary outcome; resources include their release outcome.</summary>
    public string State { get; }
    public string? Detail { get; }
    public string? BodyState { get; }
    public string? BodyDetail { get; }
    public long? OwnerId { get; }
    public long? AcquiredBy { get; }
    public string? Location { get; }
    /// <summary>The surrounding trace ID, if a tracing context was available at start.</summary>
    public string? TraceId { get; }
    /// <summary>This boundary's span ID, or its nearest traced parent for recording-only operations.</summary>
    public string? SpanId { get; }
    /// <summary>Monotonic offset from recording start to this boundary's start.</summary>
    public TimeSpan StartOffset { get; }
    /// <summary>Monotonic completion offset, or null when completion has not been observed.</summary>
    public TimeSpan? EndOffset { get; }
    /// <summary>Elapsed through snapshot capture while running, fixed at completion; includes waits and cleanup.</summary>
    public TimeSpan Elapsed { get; }
}
