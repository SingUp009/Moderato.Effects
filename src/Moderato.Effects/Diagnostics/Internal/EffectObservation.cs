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
    private readonly string kind;
    private readonly long started = Stopwatch.GetTimestamp();
    private int finished;

    private EffectObservation(string kind, Activity? activity, ActivityContext parent)
    {
        this.kind = kind;
        this.activity = activity;
        Context = activity?.Context ?? parent;
    }

    internal ActivityContext Context { get; }
    internal Activity? Activity => activity;
    internal Activation Activate() => new(activity);
    internal static bool Enabled => source.HasListeners() || completed.Enabled || duration.Enabled;

    internal static EffectObservation? Begin(string kind, RunContext context)
    {
        if (!Enabled) return null;
        var previous = System.Diagnostics.Activity.Current;
        var parent = context.Observation?.Context ?? previous?.Context ?? default;
        Activity? activity = null;
        try
        {
            activity = source.CreateActivity($"Effect {kind}", ActivityKind.Internal, parent);
            activity?.Start();
            activity?.SetTag("effect.kind", kind);
        }
        catch (Exception) { }
        finally
        {
            // Owning a resource must not replace the caller's ambient activity until release.
            SetCurrent(previous);
        }
        return activity is null && !completed.Enabled && !duration.Enabled ? null : new(kind, activity, parent);
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
        try { activity?.SetTag("effect.body.outcome", Classify(outcome)); }
        catch (Exception) { }
    }

    internal void Fault(Exception _) => End("die");

    internal void Finish<T, E>(Outcome<T, E> outcome) => End(Classify(outcome));

    private static string Classify<T, E>(Outcome<T, E> outcome) => outcome.Kind switch
    {
        OutcomeKind.Ok => "ok",
        OutcomeKind.Err => "err",
        OutcomeKind.Die => "die",
        _ => "interrupted",
    };

    private void End(string outcome)
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        try
        {
            activity?.SetTag("effect.outcome", outcome);
            if (outcome == "die") activity?.SetStatus(ActivityStatusCode.Error);
        }
        catch (Exception) { }
        try { activity?.Stop(); }
        catch (Exception) { }
        TagList tags = new() { { "effect.kind", kind }, { "effect.outcome", outcome } };
        // Diagnostics callbacks run in the application and must not replace an Effect's outcome.
        try { completed.Add(1, tags); }
        catch (Exception) { }
        try { duration.Record(seconds, tags); }
        catch (Exception) { }
    }
}
