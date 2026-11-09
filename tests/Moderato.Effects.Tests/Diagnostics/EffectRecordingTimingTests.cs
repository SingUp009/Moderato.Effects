namespace Moderato.Effects.Tests;

[Collection("Global effect tracker")]
public sealed class EffectRecordingTimingTests
{
    [Fact]
    public async Task GetSnapshot_BlockedRelease_SeparatesHoldingAndReleaseTime()
    {
        // Given
        using var recording = EffectTracker.Record();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new Buffer()), (_, _) =>
            Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                entered.TrySetResult();
                await finish.Task.ConfigureAwait(false);
                return Unit.Value;
            }));
        var effect = resource.Use(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var running = effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken).AsTask();

        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // When
            var snapshot = recording.GetSnapshot();

            // Then
            var held = Assert.Single(snapshot.Entries, entry => entry.Kind == "resource");
            var release = Assert.Single(snapshot.Entries, entry => entry.Kind == "release");
            var scope = Assert.Single(snapshot.Entries, entry => entry.Kind == "scope");
            Assert.Equal("Succeeded", scope.BodyState);
            Assert.Equal("Running", scope.State);
            Assert.True(snapshot.IsComplete);
            Assert.Equal(held.Id, release.ParentId);
            Assert.Equal("Running", held.State);
            Assert.Equal("Running", release.State);
            Assert.Null(held.EndOffset);
            Assert.Null(release.EndOffset);
            Assert.True(held.StartOffset >= TimeSpan.Zero);
            Assert.True(release.StartOffset >= held.StartOffset);
            Assert.Equal(snapshot.Elapsed - held.StartOffset, held.Elapsed);
            Assert.Equal(snapshot.Elapsed - release.StartOffset, release.Elapsed);
            Assert.Equal(held.Elapsed, release.StartOffset - held.StartOffset + release.Elapsed);

            finish.TrySetResult();
            await running;
            Assert.Null(held.EndOffset);
            Assert.Equal("Running", held.State);
        }
        finally
        {
            finish.TrySetResult();
            await running;
        }
    }

    [Fact]
    public async Task GetSnapshot_FailedRelease_FreezesMatchingCompletionTimes()
    {
        // Given
        using var recording = EffectTracker.Record();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new Buffer()), (_, _) =>
            Effect<EmptyEnvironment>.Sync<Unit>(() => throw new ReleaseException()));
        var effect = Scope<ReadUnavailable, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await scope.Acquire(resource);
            throw scope.Fail(new ReadUnavailable());
        });

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);
        var snapshot = recording.GetSnapshot();
        var later = recording.GetSnapshot();

        // Then
        var defect = Assert.IsType<Die>(exit.Value);
        Assert.IsType<ReleaseException>(defect.Exception);
        Assert.Contains(defect.Suppressed, failure => failure.Case.Value is Failed { Error: ReadUnavailable });
        var scope = Assert.Single(snapshot.Entries, entry => entry.Kind == "scope");
        var held = Assert.Single(snapshot.Entries, entry => entry.Kind == "resource");
        var release = Assert.Single(snapshot.Entries, entry => entry.Kind == "release");
        var acquisition = Assert.Single(snapshot.Entries, entry => entry.Kind == "acquire");
        Assert.Equal("Succeeded", acquisition.State);
        Assert.Equal("Succeeded", Assert.Single(snapshot.Entries, entry => entry.Id == held.AcquiredBy).State);
        Assert.Equal("Failed", scope.BodyState);
        Assert.Equal("Die", scope.State);
        Assert.Equal("Die", held.State);
        Assert.Equal("Die", release.State);
        Assert.Contains("lifetime Die ReleaseException", recording.GetTree());
        Assert.NotNull(held.EndOffset);
        Assert.Equal(held.EndOffset, release.EndOffset);
        Assert.Equal(held.Elapsed, held.EndOffset - held.StartOffset);
        Assert.Equal(held.Elapsed, release.StartOffset - held.StartOffset + release.Elapsed);
        Assert.Equal(held.Elapsed, Assert.Single(later.Entries, entry => entry.Id == held.Id).Elapsed);
        Assert.All(snapshot.Entries, entry =>
        {
            Assert.NotNull(entry.EndOffset);
            Assert.True(entry.Elapsed >= TimeSpan.Zero);
            Assert.True(entry.EndOffset <= snapshot.Elapsed);
        });
    }

    [Fact]
    public async Task GetSnapshot_CapacityExceeded_ReportsMissingHistory()
    {
        // Given
        using var recording = EffectTracker.Record(1);

        // When
        var result = await Effect<EmptyEnvironment>.Succeed(42)
            .RunAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);
        var snapshot = recording.GetSnapshot();

        // Then
        Assert.True(result.TryGetOk(out var value));
        Assert.Equal(42, value);
        Assert.Single(snapshot.Entries);
        Assert.True(snapshot.Dropped > 0 || snapshot.Evicted > 0);
        Assert.False(snapshot.IsComplete);
    }

    private sealed class Buffer;
    private sealed record ReadUnavailable;
    private sealed class ReleaseException : Exception;
}
