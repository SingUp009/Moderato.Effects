using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Moderato.Effects;
using Moderato.Results;

internal static class ObservationChecks
{
    internal static async Task RunExitAsync_Resource_ExportsStandardTelemetry()
    {
        // Given
        List<Activity> spans = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Moderato.Effects",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var count = 0L;
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Moderato.Effects") current.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((_, value, _, _) => count += value);
        metrics.Start();
        using var recording = EffectTracker.Record();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()),
            (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));

        // When
        var result = await resource.Use(_ => Effect<EmptyEnvironment>.Succeed(42)).RunExitAsync(CancellationToken.None);

        // Then
        if (!result.TryGetValue(out Ok<int> _) || count != 5 || spans.Count != 5
            || !spans.Any(span => span.GetTagItem("effect.kind") is "resource")
            || !spans.Any(span => span.GetTagItem("effect.kind") is "release"))
        {
            throw new InvalidOperationException("Packaged standard telemetry did not observe the resource lifetime.");
        }
        var snapshot = recording.GetSnapshot();
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind is not ("op" or "attempt")))
        {
            var span = spans.Single(span => span.SpanId.ToHexString() == entry.SpanId);
            if (entry.TraceId != span.TraceId.ToHexString() || Math.Abs((entry.Elapsed - span.Duration).Ticks) > 1)
                throw new InvalidOperationException("Packaged recording and standard telemetry disagree.");
        }
        var json = JsonSerializer.Serialize(snapshot, RecordingJsonContext.Default.EffectRecordingSnapshot);
        using var document = JsonDocument.Parse(json);
        var first = snapshot.Entries[0];
        var serialized = document.RootElement.GetProperty("Entries")[0];
        if (serialized.GetProperty("TraceId").GetString() != first.TraceId
            || serialized.GetProperty("SpanId").GetString() != first.SpanId)
        {
            throw new InvalidOperationException("Packaged correlation metadata must support source-generated JSON.");
        }
    }
}
