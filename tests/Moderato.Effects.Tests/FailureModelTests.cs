namespace Moderato.Effects.Tests;

public class FailureModelTests
{
    [Fact]
    public async Task RunExitAsync_BodyThrowsAnotherExceptionAfterTheCallerCancels_ReturnsDie()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var read = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            throw new IOException("stream closed");
        });

        // When
        var running = read.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<IOException>(die.Exception);
    }

    [Fact]
    public async Task RunExitAsync_AnotherTokensCancellationAfterTheCallerCancels_ReturnsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        using var service = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var call = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            await service.CancelAsync();
            service.Token.ThrowIfCancellationRequested();
            return 1;
        });

        // When
        var running = call.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task RunExitAsync_CancellationWithoutATokenAfterTheCallerCancels_ReturnsInterrupted()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var call = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            throw new OperationCanceledException();
        });

        // When
        var running = call.RunExitAsync(EmptyEnvironment.Value, cts.Token).AsTask();
        await cts.CancelAsync();
        gate.SetResult();
        var exit = await running;

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task RunExitAsync_ManyReleasesFail_KeepsAHundredSuppressedAndCountsTheRest()
    {
        // Given
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            for (var i = 0; i < 150; i++)
            {
                var n = i;
                // Registers many releases on purpose to exceed the suppressed limit.
#pragma warning disable MEFF305
                scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw new IOException($"release {n}")));
#pragma warning restore MEFF305
            }

            await Task.Yield();
            return 1;
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("release 149", die.Exception.Message);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(49, die.OmittedSuppressed);
    }
}
