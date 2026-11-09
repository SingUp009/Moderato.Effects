using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Moderato.Effects.Tests;

[Collection("Global effect tracker")]
public sealed class ObservationRecordingTests
{
    [Fact]
    public async Task GetSnapshot_WithTelemetry_MatchesSpansMetricsAndResourceEnd()
    {
        // Given
        using var parent = new Activity("request").Start();
        ConcurrentQueue<Activity> ended = new();
        using var activities = Listen(ended.Enqueue);
        List<(string Kind, double Seconds)> durations = [];
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Moderato.Effects") listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
            durations.Add(((string)tags.ToArray().Single(tag => tag.Key == "effect.kind").Value!, seconds)));
        metrics.Start();
        using var recording = EffectTracker.Record();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()), (_, _) =>
            Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                throw new IOException("private release");
            }));
        Resource<object, string, EmptyEnvironment> widened = resource;
        var effect = widened.Use(_ => Effect<EmptyEnvironment>.Fail<string>("private error"));

        // When
        var pending = effect.RunExitAsync(CancellationToken.None).AsTask();
        EffectRecordingSnapshot live;
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            live = recording.GetSnapshot();
            Assert.DoesNotContain(ended, span => span.GetTagItem("effect.kind") is "run" or "scope" or "resource" or "release");
        }
        finally { release.TrySetResult(); }
        var result = await pending;
        var final = recording.GetSnapshot();

        // Then
        Assert.True(result.TryGetValue(out Die? _));
        Assert.True(final.IsComplete);
        Assert.Equal(5, ended.Count);
        Assert.Equal(5, durations.Count);
        Assert.All(final.Entries, entry => Assert.Equal(parent.TraceId.ToHexString(), entry.TraceId));
        foreach (var entry in final.Entries.Where(entry => entry.Kind is not ("op" or "attempt")))
        {
            var span = Assert.Single(ended, span => span.SpanId.ToHexString() == entry.SpanId);
            Assert.Equal(entry.Kind, span.GetTagItem("effect.kind"));
            Assert.InRange(Math.Abs((entry.Elapsed - span.Duration).Ticks), 0, 1);
            Assert.Equal(entry.Elapsed.TotalSeconds, Assert.Single(durations, metric => metric.Kind == entry.Kind).Seconds);
            Assert.Equal(entry.State switch { "Succeeded" => "ok", "Failed" => "err", "Die" => "die", _ => "interrupted" }, span.GetTagItem("effect.outcome"));
        }
        var lifetime = Assert.Single(final.Entries, entry => entry.Kind == "resource");
        var cleanup = Assert.Single(final.Entries, entry => entry.Kind == "release");
        Assert.Equal(lifetime.EndOffset, cleanup.EndOffset);
        Assert.Equal(lifetime.Elapsed, cleanup.StartOffset - lifetime.StartOffset + cleanup.Elapsed);
        Assert.Equal("Failed", Assert.Single(live.Entries, entry => entry.Kind == "scope").BodyState);
        Assert.Equal("Running", Assert.Single(live.Entries, entry => entry.Kind == "resource").State);
        Assert.All(final.Entries.Where(entry => entry.Kind == "op"), entry => Assert.Contains(ended, span => span.SpanId.ToHexString() == entry.SpanId));
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task GetSnapshot_CapacityDropsRecords_DoesNotDropTelemetry()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        using var recording = EffectTracker.Record(1);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()),
            (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));

        // When
        var result = await resource.Use(_ => Effect<EmptyEnvironment>.Succeed(42)).RunExitAsync(CancellationToken.None);
        var snapshot = recording.GetSnapshot();

        // Then
        Assert.True(result.TryGetValue(out Ok<int> _));
        Assert.Equal(5, ended.Count);
        Assert.False(snapshot.IsComplete);
        Assert.True(snapshot.Dropped > 0);
        Assert.Equal("run", Assert.Single(snapshot.Entries).Kind);
        Assert.Equal("Succeeded", snapshot.Entries[0].State);
    }

    [Fact]
    public async Task GetSnapshot_SampledOut_PreservesRecordingAndParentCorrelation()
    {
        // Given
        using var parent = new Activity("request").Start();
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None;
        using var recording = EffectTracker.Record();

        // When
        var result = await Effect<EmptyEnvironment>.Succeed(42).WithName("Answer").RunExitAsync(CancellationToken.None);
        var snapshot = recording.GetSnapshot();

        // Then
        Assert.True(result.TryGetValue(out Ok<int> _));
        Assert.Empty(ended);
        Assert.Contains(snapshot.Entries, entry => entry.Name == "Answer");
        Assert.All(snapshot.Entries, entry => Assert.Equal(parent.TraceId.ToHexString(), entry.TraceId));
        Assert.All(snapshot.Entries, entry => Assert.Equal(parent.SpanId.ToHexString(), entry.SpanId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_ObservationModes_PreserveSynchronousExecutionContext(bool record, bool telemetry)
    {
        // Given
        using var recording = record ? EffectTracker.Record() : null;
        using var listener = telemetry ? Listen(_ => { }) : null;
        var local = new AsyncLocal<int>();
        var effect = Effect<EmptyEnvironment>.Sync(() => { local.Value = 7; return 42; });

        // When
        var pending = effect.RunAsync(CancellationToken.None);

        // Then
        Assert.Equal(7, local.Value);
        Assert.True((await pending).TryGetOk(out var value));
        Assert.Equal(42, value);
        if (recording is not null) Assert.DoesNotContain(recording.GetSnapshot().Entries, entry => entry.State == "Running");
    }

    [Fact]
    public async Task GetSnapshot_ThrowingTelemetryObserver_KeepsRecordedOutcome()
    {
        // Given
        using var listener = Listen(_ => throw new IOException("observer"));
        listener.ActivityStarted = _ => throw new IOException("observer");
        using var recording = EffectTracker.Record();

        // When
        var result = await Effect<EmptyEnvironment>.Succeed(42).RunExitAsync(CancellationToken.None);
        var snapshot = recording.GetSnapshot();

        // Then
        Assert.True(result.TryGetValue(out Ok<int> _));
        Assert.All(snapshot.Entries, entry => Assert.Equal("Succeeded", entry.State));
        Assert.All(snapshot.Entries, entry => Assert.NotNull(entry.EndOffset));
    }

    [Fact]
    public async Task GetSnapshot_NewRecording_DoesNotCaptureOldExecutions()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        using var first = EffectTracker.Record();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            entered.TrySetResult();
            await finish.Task;
            return 42;
        }).WithName("Old");

        // When
        var pending = effect.RunExitAsync(CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        first.Dispose();
        using var second = EffectTracker.Record();
        var next = await Effect<EmptyEnvironment>.Succeed(7).WithName("New").RunExitAsync(CancellationToken.None);
        finish.TrySetResult();
        var previous = await pending;

        // Then
        Assert.True(next.TryGetValue(out Ok<int> _));
        Assert.True(previous.TryGetValue(out Ok<int> _));
        var oldSnapshot = first.GetSnapshot();
        var newSnapshot = second.GetSnapshot();
        Assert.Contains(oldSnapshot.Entries, entry => entry.Name == "Old");
        Assert.DoesNotContain(newSnapshot.Entries, entry => entry.Name == "Old");
        Assert.Contains(newSnapshot.Entries, entry => entry.Name == "New");
        Assert.All(oldSnapshot.Entries.Concat(newSnapshot.Entries), entry => Assert.NotNull(entry.EndOffset));
        Assert.Equal(2, ended.Count);
    }

    [Fact]
    public async Task RunExitAsync_ObservedResource_PreservesParentBaggageWithoutExportingItAsTags()
    {
        // Given
        using var parent = new Activity("request").AddBaggage("request-key", "private context").Start();
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        using var recording = EffectTracker.Record();
        List<string?> seen = [];
        var resource = Resource.Make(Effect<EmptyEnvironment>.Sync(() =>
        {
            seen.Add(Activity.Current?.GetBaggageItem("request-key"));
            return new object();
        }), (_, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            seen.Add(Activity.Current?.GetBaggageItem("request-key"));
            return Unit.Value;
        }));

        // When
        var result = await resource.Use(_ => Effect<EmptyEnvironment>.Sync(() =>
        {
            seen.Add(Activity.Current?.GetBaggageItem("request-key"));
            return Unit.Value;
        })).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Ok<Unit> _));
        Assert.Equal(new[] { "private context", "private context", "private context" }, seen);
        Assert.DoesNotContain(ended.SelectMany(span => span.TagObjects), tag => Equals(tag.Value, "private context"));
        Assert.Same(parent, Activity.Current);
    }

    private static ActivityListener Listen(Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Moderato.Effects",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
