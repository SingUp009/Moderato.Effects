namespace Moderato.Effects.Tests;

public class RunAsyncBoundaryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RunAsync_InvalidNeverOutcome_ReturnsAFaultedValueTask(bool error, bool pending, bool cancelable)
    {
        // Given
        using var caller = new CancellationTokenSource();
        var token = cancelable ? caller.Token : CancellationToken.None;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> effect;
        if (error)
        {
            var source = Effect<EmptyEnvironment>.FromResultAsync<int, Never>(async _ =>
            {
                if (pending) await completion.Task;
                return Result.Err((Never)null!);
            });
            effect = source;
        }
        else
        {
            Effect<Never, string, EmptyEnvironment> source = Effect<EmptyEnvironment>.Async<Never>(async _ =>
            {
                if (pending) await completion.Task;
                return null!;
            });
            effect = source;
        }
        ValueTask<Result<int, string>> running = default;

        // When
        var thrown = Record.Exception(() => { running = effect.RunAsync(token); });
        if (pending)
        {
            Assert.False(running.IsCompleted);
            completion.SetResult();
        }

        // Then
        Assert.Null(thrown);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running.AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RunAsync_DefaultEffect_RejectsInvalidDescription()
    {
        // Given
        Effect<int, Never, EmptyEnvironment> effect = default;

        // When
        Action act = () => { _ = effect.RunAsync(CancellationToken.None); };

        // Then
        Assert.Throws<InvalidOperationException>(act);
    }
}
