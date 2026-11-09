using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    }
}
