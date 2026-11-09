using System.Text.Json;
using System.Text.Json.Serialization;
using Moderato.Effects;
using Moderato.Results;

internal static class RecordingTimingChecks
{
    internal static async Task GetSnapshot_BlockedFailingRelease_PreservesFrozenDurations()
    {
        // Given
        using var recording = EffectTracker.Record();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionTimeout = TimeSpan.FromSeconds(10);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()), (_, _) =>
            Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                entered.TrySetResult();
                await finish.Task.ConfigureAwait(false);
                throw new ReleaseFailure();
            }));
        var effect = Scope<string, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await scope.Acquire(resource);
            throw scope.Fail("body-failure");
        });

        // When
        var running = effect.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None).AsTask();
        EffectRecordingSnapshot live;
        try
        {
            await entered.Task.WaitAsync(completionTimeout);
            live = recording.GetSnapshot();
        }
        finally
        {
            finish.TrySetResult();
            await running.WaitAsync(completionTimeout);
        }
        var exit = await running;
        var completed = recording.GetSnapshot();
        var later = recording.GetSnapshot();
        var json = JsonSerializer.Serialize(completed, RecordingJsonContext.Default.EffectRecordingSnapshot);

        // Then
        var liveResource = live.Entries.Single(entry => entry.Kind == "resource");
        var liveRelease = live.Entries.Single(entry => entry.Kind == "release");
        var held = completed.Entries.Single(entry => entry.Kind == "resource");
        var release = completed.Entries.Single(entry => entry.Kind == "release");
        var acquisition = completed.Entries.Single(entry => entry.Kind == "acquire");
        if (exit.Value is not Die { Exception: ReleaseFailure } die
            || !die.Suppressed.Any(failure => failure.Case.Value is Failed { Error: "body-failure" })
            || !live.IsComplete || !completed.IsComplete
            || liveResource.State != "Running" || liveResource.EndOffset is not null
            || liveRelease.State != "Running" || liveRelease.EndOffset is not null
            || liveResource.Elapsed != live.Elapsed - liveResource.StartOffset
            || liveRelease.Elapsed != live.Elapsed - liveRelease.StartOffset
            || held.State != "Die" || release.State != "Die"
            || acquisition.State != "Succeeded"
            || held.EndOffset is null || held.EndOffset != release.EndOffset
            || held.Elapsed != release.StartOffset - held.StartOffset + release.Elapsed
            || held.Elapsed != later.Entries.Single(entry => entry.Id == held.Id).Elapsed
            || completed.Entries.Any(entry => entry.EndOffset is null || entry.Elapsed < TimeSpan.Zero))
        {
            throw new InvalidOperationException("Packaged recording must preserve timing, immutable snapshots and cleanup failures.");
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("Entries").GetArrayLength() != completed.Entries.Count
            || !recording.GetTree().Contains("elapsed ", StringComparison.Ordinal)
            || !recording.GetTree().Contains("lifetime Die ReleaseFailure", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Packaged timing metadata must support source-generated JSON and text rendering.");
        }
    }

    private sealed class ReleaseFailure : Exception;
}

[JsonSerializable(typeof(EffectRecordingSnapshot), GenerationMode = JsonSourceGenerationMode.Serialization)]
internal sealed partial class RecordingJsonContext : JsonSerializerContext;
