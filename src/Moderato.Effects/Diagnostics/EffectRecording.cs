using System.Diagnostics;
using System.Globalization;
using System.Text;
using Moderato.Effects.Internal;

namespace Moderato.Effects;

/// <summary>Records bounded execution trees without retaining application values.</summary>
public sealed class EffectRecording : IDisposable
{
    private const int MAX_TEXT_LENGTH = 160;

    private readonly Lock gate = new();
    private readonly Dictionary<long, TrackingNode> nodes = [];
    private readonly int capacity;
    private readonly long startTimestamp = Stopwatch.GetTimestamp();
    private long nextId;
    private long dropped;
    private long evicted;

    internal EffectRecording(int capacity) => this.capacity = capacity;

    internal TrackingNode? Begin(TrackingNode? parent, string kind, string name, long? owner, long? acquiredBy, string? location, long startedAt, string? traceId, string? spanId)
    {
        try
        {
            lock (gate)
            {
                if (nodes.Count == capacity)
                {
                    var candidate = nodes.Values.Where(static node => node.State != "Running").MinBy(static node => node.Id);
                    if (candidate is null)
                    {
                        dropped++;
                        return null;
                    }
                    nodes.Remove(candidate.Id);
                    evicted++;
                }
                var node = new TrackingNode(this, ++nextId, parent?.Id, kind, Limit(name), owner, acquiredBy, location is null ? null : Limit(location), startedAt, traceId, spanId);
                nodes.Add(node.Id, node);
                return node;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref dropped);
            return null;
        }
    }

    internal TimeSpan Elapsed(long startedAt, long endedAt)
        => Stopwatch.GetElapsedTime(startTimestamp, endedAt) - Stopwatch.GetElapsedTime(startTimestamp, startedAt);

    internal void Finish(TrackingNode node, OutcomeKind outcome, string? detail, long endedAt, bool body = false)
    {
        lock (gate) SetResultCore(node, State(outcome), detail, body, endedAt);
    }

    internal void FinishRelease(TrackingNode? release, TrackingNode? resource, OutcomeKind outcome, string? detail, long endedAt)
    {
        lock (gate)
        {
            if (release is not null) SetResultCore(release, State(outcome), detail, false, endedAt);
            if (resource is not null) SetResultCore(resource, State(outcome), detail, false, endedAt);
        }
    }

    private static string State(OutcomeKind outcome) => outcome switch
    {
        OutcomeKind.Ok => "Succeeded",
        OutcomeKind.Err => "Failed",
        OutcomeKind.Die => "Die",
        _ => "Interrupted",
    };

    private static void SetResultCore(TrackingNode node, string state, string? detail, bool body, long endedAt)
    {
        // Final observations freeze the row; scope bodies are recorded before cleanup starts.
        if (node.EndTimestamp is not null) return;
        if (body)
        {
            node.BodyState = state;
            node.BodyDetail = detail;
        }
        else
        {
            node.State = state;
            node.Detail = detail;
            node.EndTimestamp = endedAt;
        }
    }

    private static string Limit(string text) => text.Length <= MAX_TEXT_LENGTH ? text : text[..MAX_TEXT_LENGTH];

    /// <summary>Copies retained metadata and timing at one monotonic capture instant.</summary>
    public EffectRecordingSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp, Stopwatch.GetTimestamp());
            var entries = nodes.Values.OrderBy(static node => node.Id)
                .Select(node => new EffectRecordingEntry(node, startTimestamp, elapsed)).ToArray();
            return new(elapsed, Array.AsReadOnly(entries), dropped, evicted);
        }
    }

    /// <summary>Returns a coherent tree containing active and retained completed executions.</summary>
    public string GetTree()
    {
        var snapshot = GetSnapshot();
        var text = new StringBuilder();
        text.Append("capture roots-started-during-recording  dropped ").Append(snapshot.Dropped)
            .Append("  evicted ").Append(snapshot.Evicted).Append("  incomplete ")
            .AppendLine(snapshot.IsComplete ? "no" : "yes");
        var ordered = snapshot.Entries;
        var known = ordered.Select(static node => node.Id).ToHashSet();
        var children = ordered.ToLookup(static node => node.ParentId ?? 0);
        var pending = new Stack<(EffectRecordingEntry Node, string Prefix, bool Last, bool Root)>();
        foreach (var root in ordered.Where(node => node.ParentId is null || !known.Contains(node.ParentId.Value)).Reverse())
            pending.Push((root, "", true, true));
        while (pending.TryPop(out var entry))
        {
            var (node, prefix, last, root) = entry;
            text.Append(prefix).Append(root ? "" : last ? "└─ " : "├─ ")
                .Append(node.Kind).Append(" #").Append(node.Id).Append(' ').Append(node.Name).Append("  ");
            if (node.BodyState is not null)
            {
                text.Append("body ").Append(node.BodyState);
                if (node.BodyDetail is not null) text.Append(' ').Append(node.BodyDetail);
                text.Append("  final ");
            }
            if (node.Kind == "resource") text.Append("lifetime ");
            text.Append(node.State);
            if (node.Detail is not null) text.Append(' ').Append(node.Detail);
            if (node.OwnerId is { } owner)
            {
                text.Append("  owner #").Append(owner).Append(known.Contains(owner) ? "" : " unavailable");
            }
            if (node.AcquiredBy is { } acquiredBy)
            {
                text.Append(node.Kind == "fiber" ? "  spawned-by #" : "  acquired-by #").Append(acquiredBy)
                    .Append(known.Contains(acquiredBy) ? "" : " unavailable");
            }
            if (node.ParentId is { } parent && !known.Contains(parent)) text.Append("  parent #").Append(parent).Append(" unavailable");
            if (node.Location is not null) text.Append("  ").Append(node.Location);
            text.Append("  elapsed ").Append(node.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append("ms");
            text.AppendLine();
            var entries = children[node.Id].ToArray();
            for (var i = entries.Length - 1; i >= 0; i--)
                pending.Push((entries[i], prefix + (root ? "" : last ? "   " : "│  "), i == entries.Length - 1, false));
        }
        return text.ToString();
    }

    /// <summary>Stops capturing new roots; already captured executions can still finish.</summary>
    public void Dispose() => EffectTracker.StopRecording(this);
}

internal sealed class TrackingNode(EffectRecording recording, long id, long? parentId, string kind, string name,
    long? ownerId, long? acquiredBy, string? location, long startedAt, string? traceId, string? spanId)
{
    internal EffectRecording Recording { get; } = recording;
    internal long Id { get; } = id;
    internal long? ParentId { get; } = parentId;
    internal string Kind { get; } = kind;
    internal string Name { get; } = name;
    internal long? OwnerId { get; } = ownerId;
    internal long? AcquiredBy { get; } = acquiredBy;
    internal string? Location { get; } = location;
    internal long StartTimestamp { get; } = startedAt;
    internal string? TraceId { get; } = traceId;
    internal string? SpanId { get; } = spanId;
    internal long? EndTimestamp { get; set; }
    internal string State { get; set; } = "Running";
    internal string? Detail { get; set; }
    internal string? BodyState { get; set; }
    internal string? BodyDetail { get; set; }
}
