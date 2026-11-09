using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Moderato.Results;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class ObservationBenchmarks
{
    [Params("Disabled", "Recording", "Metrics", "Traces", "Combined")]
    public string Mode { get; set; } = "Disabled";

    private ActivityListener? activities;
    private MeterListener? metrics;
    private EffectRecording? recording;
    private readonly Effect<int, Never, int> effect = Effect<int>.Succeed(42);

    [GlobalSetup]
    public void Setup()
    {
        if (Mode is "Recording" or "Combined") recording = EffectTracker.Record();
        if (Mode is "Traces" or "Combined")
        {
            activities = new()
            {
                ShouldListenTo = source => source.Name == "Moderato.Effects",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(activities);
        }
        if (Mode is "Metrics" or "Combined")
        {
            metrics = new();
            metrics.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Moderato.Effects") listener.EnableMeasurementEvents(instrument);
            };
            metrics.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            metrics.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            metrics.Start();
        }
        if (!Run().Result.TryGetValue(out Ok<int> ok) || ok.Value != 42)
            throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark]
    public ValueTask<Exit<int, Never>> Run() => effect.RunExitAsync(42);

    [GlobalCleanup]
    public void Cleanup()
    {
        activities?.Dispose();
        metrics?.Dispose();
        recording?.Dispose();
    }
}
