using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Moderato.Effects.Tests;

[Collection("Global effect tracker")]
public sealed class ObservationTests
{
    [Fact]
    public async Task RunExitAsync_ResourceReleaseWaits_KeepsLifetimesOpenAndPreservesParent()
    {
        // Given
        using var parent = new Activity("request").Start();
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()), (_, _) =>
            Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                entered.SetResult();
                await release.Task;
                return Unit.Value;
            }));
        var effect = resource.Use(_ => Effect<EmptyEnvironment>.Succeed(42));

        // When
        var pending = effect.RunExitAsync(CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(ended, span => span.GetTagItem("effect.kind") is "run" or "scope" or "resource" or "release");
        }
        finally { release.TrySetResult(); }
        var result = await pending;

        // Then
        Assert.True(result.TryGetValue(out Ok<int> _));
        Assert.Same(parent, Activity.Current);
        var root = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "run");
        var scope = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "scope");
        var lifetime = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "resource");
        var cleanup = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "release");
        Assert.Equal(parent.SpanId, root.ParentSpanId);
        Assert.Equal(root.SpanId, scope.ParentSpanId);
        Assert.Equal(scope.SpanId, lifetime.ParentSpanId);
        Assert.Equal(lifetime.SpanId, cleanup.ParentSpanId);
        Assert.All(ended, span => Assert.Equal(parent.TraceId, span.TraceId));
        Assert.Equal("ok", scope.GetTagItem("effect.body.outcome"));
    }

    [Fact]
    public async Task RunExitAsync_BodyErrAndReleaseDie_ReportsFinalOutcomeWithoutPayloads()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()), (_, _) =>
            Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException("private message")));
        Resource<object, string, EmptyEnvironment> widened = resource;
        var effect = widened.Use(_ => Effect<EmptyEnvironment>.Fail<string>("private error"));

        // When
        var result = await effect.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Die? _));
        var scope = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "scope");
        Assert.Equal("err", scope.GetTagItem("effect.body.outcome"));
        Assert.Equal("die", scope.GetTagItem("effect.outcome"));
        Assert.Equal(ActivityStatusCode.Error, scope.Status);
        Assert.DoesNotContain(ended.SelectMany(span => span.TagObjects), tag => tag.Value?.ToString()?.Contains("private", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RunExitAsync_CanceledCaller_RecordsInterruptionWithoutErrorStatus()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // When
        var result = await Effect<EmptyEnvironment>.Succeed(42).RunExitAsync(cancellation.Token);

        // Then
        Assert.True(result.TryGetValue(out Interrupted? _));
        var root = Assert.Single(ended);
        Assert.Equal("interrupted", root.GetTagItem("effect.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, root.Status);
    }

    [Fact]
    public async Task RunAsync_ThrowingObservers_PreservesSuccessAndAmbientActivity()
    {
        // Given
        using var parent = new Activity("request").Start();
        using var listener = Listen(_ => throw new IOException("observer"));
        listener.ActivityStarted = _ => throw new IOException("observer");
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, current) => { if (instrument.Meter.Name == "Moderato.Effects") current.EnableMeasurementEvents(instrument); };
        metrics.SetMeasurementEventCallback<long>((_, _, _, _) => throw new IOException("metric"));
        metrics.SetMeasurementEventCallback<double>((_, _, _, _) => throw new IOException("metric"));
        metrics.Start();

        // When
        var result = await Effect<EmptyEnvironment>.Succeed(42).RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var value));
        Assert.Equal(42, value);
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task RunExitAsync_ConcurrentExecutions_DoesNotParentOneRunUnderAnother()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        using var parent = new Activity("request").Start();
        var effect = Effect<EmptyEnvironment>.Async<int>(async _ => { await Task.Yield(); return 42; });

        // When
        await Task.WhenAll(effect.RunExitAsync(CancellationToken.None).AsTask(), effect.RunExitAsync(CancellationToken.None).AsTask());

        // Then
        Assert.Equal(2, ended.Count);
        Assert.All(ended, span => Assert.Equal(parent.SpanId, span.ParentSpanId));
        Assert.Equal(2, ended.Select(span => span.SpanId).Distinct().Count());
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task RunExitAsync_MetricsOnly_RecordsBoundedDimensionsWithoutTracker()
    {
        // Given
        List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> values = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) => { if (instrument.Meter.Name == "Moderato.Effects") current.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => values.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => values.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();

        // When
        var result = await Effect<EmptyEnvironment>.Fail<string>("private").RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Err<string> _));
        Assert.Equal(2, values.Count);
        Assert.Contains(values, value => value.Name == "moderato.effects.completed" && value.Value == 1);
        Assert.Contains(values, value => value.Name == "moderato.effects.duration" && value.Value >= 0);
        Assert.All(values, value => Assert.Equal(new[] { "effect.kind", "effect.outcome" }, value.Tags.Select(tag => tag.Key)));
        Assert.All(values, value => Assert.Equal("err", value.Tags.Single(tag => tag.Key == "effect.outcome").Value));
        Assert.False(EffectTracker.EnableTracking);
    }

    [Fact]
    public async Task RunExitAsync_FailedAcquisition_DoesNotReportResourceOrRelease()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Fail<string>("unavailable"),
            (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));

        // When
        var result = await resource.Use(_ => Effect<EmptyEnvironment>.Succeed(42)).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Err<string> _));
        Assert.Equal("err", Assert.Single(ended, span => span.GetTagItem("effect.kind") is "acquire").GetTagItem("effect.outcome"));
        Assert.DoesNotContain(ended, span => span.GetTagItem("effect.kind") is "resource" or "release");
    }

    [Fact]
    public async Task RunExitAsync_ForkedChild_KeepsOwnerAndFinalResult()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var fiber = await scope.Fork(Effect<EmptyEnvironment>.Async<int>(async _ => { await Task.Yield(); return 42; }));
            return await scope.Run(fiber.Join());
        });

        // When
        var result = await effect.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Ok<int> ok));
        Assert.Equal(42, ok.Value);
        var fiber = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "fiber");
        var owner = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "scope");
        Assert.Equal(owner.SpanId, fiber.ParentSpanId);
        Assert.Equal("ok", fiber.GetTagItem("effect.outcome"));
    }

    [Fact]
    public async Task RunExitAsync_SamplingDisabled_PreservesResultWithoutSpans()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None;

        // When
        var result = await Effect<EmptyEnvironment>.Succeed(42).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Ok<int> _));
        Assert.Empty(ended);
    }

    [Fact]
    public async Task RunExitAsync_AsyncBoundaries_ExposeTheirActivityAndRestoreCallerContext()
    {
        // Given
        using var parent = new Activity("request").Start();
        using var listener = Listen(_ => { });
        var seen = new ConcurrentQueue<string>();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Async<object>(async _ =>
        {
            await Task.Yield();
            seen.Enqueue((string)Activity.Current!.GetTagItem("effect.kind")!);
            return new object();
        }), (_, _) => Effect<EmptyEnvironment>.Async<Unit>(async _ =>
        {
            await Task.Yield();
            seen.Enqueue((string)Activity.Current!.GetTagItem("effect.kind")!);
            return Unit.Value;
        }));
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(resource);
            seen.Enqueue((string)Activity.Current!.GetTagItem("effect.kind")!);
            var fiber = await scope.Fork(Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                await Task.Yield();
                seen.Enqueue((string)Activity.Current!.GetTagItem("effect.kind")!);
                return Unit.Value;
            }));
            return await scope.Run(fiber.Join());
        });

        // When
        var result = await effect.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Ok<Unit> _));
        Assert.Equal(new[] { "acquire", "scope", "fiber", "release" }, seen);
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task RunExitAsync_ChildDefectAndThrowingCancellationCallback_ClosesFiberOnce()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        var failChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var defect = new IOException("child");
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            using var registration = scope.CancellationToken.Register(() => throw new InvalidOperationException("callback"));
            var fiber = await scope.Fork(Effect<EmptyEnvironment>.Async<int>(async _ =>
            {
                await failChild.Task;
                throw defect;
            }));
            failChild.SetResult();
            return await scope.Run(fiber.Join());
        });

        // When
        var result = await effect.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Die? failure));
        Assert.Same(defect, failure.Exception);
        var fiber = Assert.Single(ended, span => span.GetTagItem("effect.kind") is "fiber");
        Assert.Equal("die", fiber.GetTagItem("effect.outcome"));
        Assert.Equal(ActivityStatusCode.Error, fiber.Status);
        Assert.Equal(ended.Count, ended.Select(span => span.SpanId).Distinct().Count());
    }

    [Theory]
    [InlineData("ok", "Succeeded", ActivityStatusCode.Unset)]
    [InlineData("err", "Failed", ActivityStatusCode.Unset)]
    [InlineData("die", "Die", ActivityStatusCode.Error)]
    [InlineData("interrupted", "Interrupted", ActivityStatusCode.Unset)]
    public async Task RunExitAsync_Outcome_UsesStableRecordingAndTelemetryVocabulary(string outcome, string state, ActivityStatusCode status)
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var activities = Listen(ended.Enqueue);
        List<Instrument> instruments = [];
        List<(Instrument Instrument, double Value, KeyValuePair<string, object?>[] Tags)> measurements = [];
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != "Moderato.Effects" || instrument.Name is not ("moderato.effects.completed" or "moderato.effects.duration")) return;
            instruments.Add(instrument);
            listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Add((instrument, value, tags.ToArray())));
        metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Add((instrument, value, tags.ToArray())));
        metrics.Start();
        using var recording = EffectTracker.Record();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (outcome == "interrupted") caller.Cancel();
        Effect<int, string, EmptyEnvironment> effect = outcome switch
        {
            "err" => Effect<EmptyEnvironment>.Fail<string>("private error"),
            "die" => Effect<EmptyEnvironment>.Sync<int>(() => throw new IOException("private message")),
            _ => Effect<EmptyEnvironment>.Succeed(42),
        };

        // When
        await effect.WithName("private application name").RunExitAsync(EmptyEnvironment.Value, caller.Token);

        // Then
        var root = Assert.Single(recording.GetSnapshot().Entries, entry => entry.Kind == "run");
        Assert.Equal(state, root.State);
        var span = Assert.Single(ended, activity => activity.GetTagItem("effect.kind") is "run");
        Assert.Equal("Moderato.Effects", span.Source.Name);
        Assert.Equal("Effect run", span.OperationName);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal(status, span.Status);
        Assert.Equal("run", span.GetTagItem("effect.kind"));
        Assert.Equal(outcome, span.GetTagItem("effect.outcome"));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string text && text.Contains("private", StringComparison.Ordinal));
        Assert.Collection(instruments.OrderBy(instrument => instrument.Name, StringComparer.Ordinal),
            instrument =>
            {
                Assert.IsType<Counter<long>>(instrument);
                Assert.Equal("moderato.effects.completed", instrument.Name);
                Assert.Equal("{boundary}", instrument.Unit);
            },
            instrument =>
            {
                Assert.IsType<Histogram<double>>(instrument);
                Assert.Equal("moderato.effects.duration", instrument.Name);
                Assert.Equal("s", instrument.Unit);
            });
        var runs = measurements.Where(measurement => measurement.Tags.Any(tag => tag.Key == "effect.kind" && tag.Value is "run")).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Equal(1, Assert.Single(runs, measurement => measurement.Instrument.Name == "moderato.effects.completed").Value);
        Assert.Equal(root.Elapsed.TotalSeconds, Assert.Single(runs, measurement => measurement.Instrument.Name == "moderato.effects.duration").Value);
        Assert.All(runs, measurement =>
        {
            Assert.Equal(new[] { "effect.kind", "effect.outcome" }, measurement.Tags.Select(tag => tag.Key).Order(StringComparer.Ordinal));
            Assert.Equal("run", measurement.Tags.Single(tag => tag.Key == "effect.kind").Value);
            Assert.Equal(outcome, measurement.Tags.Single(tag => tag.Key == "effect.outcome").Value);
        });
    }

    [Fact]
    public async Task RunExitAsync_RecordedResourceAndRepeatedFiber_UsesStableKindsOwnershipAndTraceCorrelation()
    {
        // Given
        ConcurrentQueue<Activity> ended = new();
        using var listener = Listen(ended.Enqueue);
        using var recording = EffectTracker.Record();
        var resource = Resource.Make(Effect<DefaultEnvironment>.Succeed(new object()),
            (_, _) => Effect<DefaultEnvironment>.Succeed(Unit.Value));
        var effect = Scope<Never, DefaultEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(resource);
            var child = await scope.Fork(Effect<DefaultEnvironment>.Succeed(42).Repeat(Schedule.Immediately.UpTo(0)));
            return await scope.Run(child.Join());
        });

        // When
        await effect.RunExitAsync(DefaultEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        var entries = recording.GetSnapshot().Entries;
        Assert.All(new[] { "acquire", "attempt", "fiber", "op", "release", "resource", "run", "scope" },
            kind => Assert.Contains(entries, entry => entry.Kind == kind));
        Assert.All(new[] { "acquire", "fiber", "release", "resource", "run", "scope" },
            kind => Assert.Contains(ended, span => Equals(span.GetTagItem("effect.kind"), kind)));
        Assert.DoesNotContain(ended, span => span.GetTagItem("effect.kind") is "op" or "attempt");
        Assert.All(ended, span =>
        {
            Assert.Equal($"Effect {span.GetTagItem("effect.kind")}", span.OperationName);
            Assert.Equal(ActivityKind.Internal, span.Kind);
        });
        var owner = Assert.Single(entries, entry => entry.Kind == "scope");
        var lifetime = Assert.Single(entries, entry => entry.Kind == "resource");
        var fiber = Assert.Single(entries, entry => entry.Kind == "fiber");
        Assert.Equal(owner.Id, lifetime.OwnerId);
        Assert.Equal(owner.Id, fiber.OwnerId);
        var origin = Assert.Single(entries, entry => entry.Id == lifetime.AcquiredBy);
        Assert.Equal("Succeeded", origin.State);
        var correlatedAcquisitionSpan = Assert.Single(ended, span => span.SpanId.ToHexString() == origin.SpanId);
        Assert.Equal("acquire", correlatedAcquisitionSpan.GetTagItem("effect.kind"));
        Assert.All(entries.Where(entry => entry.Kind is "op" or "attempt"), entry =>
            Assert.Contains(ended, span => span.SpanId.ToHexString() == entry.SpanId));
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
