namespace Moderato.Effects.Tests;

public class RunExitBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunExitAsync_InvalidNeverValue_ReturnsDie(bool pending, bool cancelable)
    {
        // Given
        using var caller = new CancellationTokenSource();
        var token = cancelable ? caller.Token : CancellationToken.None;
        var completion = new TaskCompletionSource<Never>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = pending
            ? Effect<EmptyEnvironment>.Async<Never>(_ => new(completion.Task))
            : Effect<EmptyEnvironment>.Sync<Never>(static () => null!);
        Effect<int, Never, EmptyEnvironment> effect = source;

        // When
        var running = effect.RunExitAsync(token);
        if (pending)
        {
            Assert.False(running.IsCompleted);
            completion.SetResult(null!);
        }
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Empty(die.Suppressed);
        Assert.Equal(0, die.OmittedSuppressed);
    }

    [Fact]
    public void RunExitAsync_DefaultEffect_RejectsInvalidDescription()
    {
        // Given
        Effect<int, Never, EmptyEnvironment> effect = default;

        // When
        Action act = () => { _ = effect.RunExitAsync(CancellationToken.None); };

        // Then
        Assert.Throws<InvalidOperationException>(act);
    }
}
