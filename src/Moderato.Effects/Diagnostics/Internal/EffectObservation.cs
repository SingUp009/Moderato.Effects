using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Moderato.Effects.Internal;

internal sealed class EffectObservation
{
    private static readonly ActivitySource source = new("Moderato.Effects");
    private static readonly Meter meter = new("Moderato.Effects");
    private static readonly Counter<long> completed = meter.CreateCounter<long>("moderato.effects.completed", "{boundary}");
    private static readonly Histogram<double> duration = meter.CreateHistogram<double>("moderato.effects.duration", "s");
    private readonly Activity? activity;
    private readonly Activity? parentActivity;
    private readonly TrackingNode? node;
    private readonly TrackingNode? recordingParent;
    private readonly string kind;
    private readonly long started;
    private readonly DateTime utcStart;
    private readonly bool standard;
    private int finished;

    private EffectObservation(string kind, Activity? activity, Activity? parent, TrackingNode? node,
        TrackingNode? recordingParent, long started, DateTime utcStart, bool standard)
    {
        this.kind = kind;
        this.activity = activity;
        parentActivity = parent;
        this.node = node;
        this.recordingParent = node ?? recordingParent;
        this.started = started;
        this.utcStart = utcStart;
        this.standard = standard;
    }

    private Activity? ContextActivity => activity ?? parentActivity;
    internal Activity? Activity => activity;
    internal bool HasRecording => recordingParent is not null;
    internal Activation Activate() => new(activity);
    internal static bool Enabled => EffectTracker.Recording is not null || StandardEnabled;
    private static bool StandardEnabled => source.HasListeners() || completed.Enabled || duration.Enabled;

    internal static EffectObservation? BeginRoot(RunContext context, string name)
        => BeginCore("run", context, name, EffectTracker.Recording, null, null, null, null);

    internal static EffectObservation? BeginResource<T>(string kind, RunContext context,
        EffectObservation? owner, EffectObservation? acquiredBy = null)
    {
        // Type.Name can rebuild a collectible runtime cache, even with observation disabled.
        var name = context.Observation?.HasRecording == true ? typeof(T).Name : null;
        return Begin(kind, context, name, owner, acquiredBy);
    }

    internal static EffectObservation? Begin(string kind, RunContext context, string? name = null,
        EffectObservation? owner = null, EffectObservation? acquiredBy = null, string? location = null)
    {
        var parent = context.Observation?.recordingParent;
        return BeginCore(kind, context, name ?? kind, parent?.Recording, parent, owner?.recordingParent?.Id,
            acquiredBy?.recordingParent?.Id, location);
    }

    private static EffectObservation? BeginCore(string kind, RunContext context, string name, EffectRecording? recording,
        TrackingNode? recordingParent, long? owner, long? acquiredBy, string? location)
    {
        var standard = kind is not ("op" or "attempt") && StandardEnabled;
        if (recording is null && !standard) return null;
        var started = Stopwatch.GetTimestamp();
        var previous = System.Diagnostics.Activity.Current;
        var parent = context.Observation?.ContextActivity ?? previous;
        var utcStart = default(DateTime);
        Activity? activity = null;
        if (standard)
        {
            try
            {
                // Choosing the parent Activity preserves baggage as well as trace identity.
                SetCurrent(parent);
                activity = source.CreateActivity($"Effect {kind}", ActivityKind.Internal);
                if (activity is not null)
                {
                    utcStart = DateTime.UtcNow - Stopwatch.GetElapsedTime(started);
                    activity.SetStartTime(utcStart);
                }
                activity?.Start();
                activity?.SetTag("effect.kind", kind);
            }
            catch (Exception) { }
            finally
            {
                // Owning a resource must not replace the caller's ambient activity until release.
                SetCurrent(previous);
            }
        }
        var correlation = activity?.Context ?? parent?.Context ?? default;
        var node = recording?.Begin(recordingParent, kind, name, owner, acquiredBy, location, started,
            correlation.TraceId == default ? null : correlation.TraceId.ToHexString(),
            correlation.SpanId == default ? null : correlation.SpanId.ToHexString());
        return node is null && activity is null && !(standard && (completed.Enabled || duration.Enabled))
            ? null : new(kind, activity, parent, node, recordingParent, started, utcStart, standard);
    }

    internal static void SetCurrent(Activity? value)
    {
        try { System.Diagnostics.Activity.Current = value; }
        catch (Exception) { }
    }

    internal readonly struct Activation : IDisposable
    {
        private readonly Activity? previous;
        private readonly bool changed;

        internal Activation(Activity? activity)
        {
            previous = System.Diagnostics.Activity.Current;
            changed = activity is not null;
            if (changed) SetCurrent(activity);
        }

        public void Dispose()
        {
            if (changed) SetCurrent(previous);
        }
    }

    internal void Body<T, E>(Outcome<T, E> outcome)
    {
        if (Volatile.Read(ref finished) != 0) return;
        node?.Recording.Finish(node, outcome.Kind, Detail(outcome, node is not null), Stopwatch.GetTimestamp(), body: true);
        try { activity?.SetTag("effect.body.outcome", Classify(outcome.Kind)); }
        catch (Exception) { }
    }

    internal void Fault(Exception exception)
        => End(OutcomeKind.Die, node is null ? null : exception.GetType().Name, Stopwatch.GetTimestamp());

    internal void Finish<T, E>(Outcome<T, E> outcome)
        => End(outcome.Kind, Detail(outcome, node is not null), Stopwatch.GetTimestamp());

    internal static void FinishRelease<T, E>(EffectObservation? release, EffectObservation? resource, Outcome<T, E> outcome)
    {
        if (release is null && resource is null) return;
        var endedAt = Stopwatch.GetTimestamp();
        var releaseEnded = release is not null && Interlocked.Exchange(ref release.finished, 1) == 0;
        var resourceEnded = resource is not null && Interlocked.Exchange(ref resource.finished, 1) == 0;
        var releaseNode = releaseEnded ? release!.node : null;
        var resourceNode = resourceEnded ? resource!.node : null;
        var detail = Detail(outcome, releaseNode is not null || resourceNode is not null);
        (releaseNode?.Recording ?? resourceNode?.Recording)?.FinishRelease(releaseNode, resourceNode, outcome.Kind, detail, endedAt);
        if (releaseEnded) release!.Emit(outcome.Kind, endedAt);
        if (resourceEnded) resource!.Emit(outcome.Kind, endedAt);
    }

    private static string? Detail<T, E>(Outcome<T, E> outcome, bool recording) => !recording ? null : outcome.Kind switch
    {
        OutcomeKind.Err => outcome.Error?.GetType().Name,
        OutcomeKind.Die => outcome.Defect?.SourceException.GetType().Name,
        _ => null,
    };

    private static string Classify(OutcomeKind outcome) => outcome switch
    {
        OutcomeKind.Ok => "ok",
        OutcomeKind.Err => "err",
        OutcomeKind.Die => "die",
        _ => "interrupted",
    };

    private void End(OutcomeKind outcome, string? detail, long endedAt)
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        node?.Recording.Finish(node, outcome, detail, endedAt);
        Emit(outcome, endedAt);
    }

    private void Emit(OutcomeKind outcome, long endedAt)
    {
        if (!standard) return;
        var elapsed = node is not null ? node.Recording.Elapsed(started, endedAt) : Stopwatch.GetElapsedTime(started, endedAt);
        var state = Classify(outcome);
        var previous = System.Diagnostics.Activity.Current;
        try
        {
            activity?.SetTag("effect.outcome", state);
            if (outcome == OutcomeKind.Die) activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetEndTime(utcStart + elapsed);
        }
        catch (Exception) { }
        try { activity?.Stop(); }
        catch (Exception) { }
        finally { SetCurrent(previous); }
        TagList tags = new() { { "effect.kind", kind }, { "effect.outcome", state } };
        // Diagnostics callbacks run in the application and must not replace an Effect's outcome.
        try { completed.Add(1, tags); }
        catch (Exception) { }
        try { duration.Record(elapsed.TotalSeconds, tags); }
        catch (Exception) { }
    }
}
