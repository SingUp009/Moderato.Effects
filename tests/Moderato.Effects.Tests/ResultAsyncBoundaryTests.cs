namespace Moderato.Effects.Tests;

public class ResultAsyncBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromResultAsync_PendingFailureWithoutCallerIsolation_ReturnsOriginalDefect(bool cancellationException)
    {
        // Given
        Exception failure = cancellationException ? new OperationCanceledException() : new IOException("failed");
        var completion = new TaskCompletionSource<Result<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(completion.Task));

        // When
        // None keeps caller isolation from masking the inner exception boundary.
        var running = effect.RunExitAsync(CancellationToken.None);
        Assert.False(running.IsCompleted);
        completion.SetException(failure);
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Empty(die.Suppressed);
    }
}
