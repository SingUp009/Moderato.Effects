namespace Moderato.Effects.Tests;

public class UnionContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatchCase_InfallibleRecovery_KeepsTheCaseAndCompletionMode(bool pending)
    {
        // Given
        Effect<int, AppError, EmptyEnvironment> failed = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(7));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = Effect<EmptyEnvironment>.Async(_ => pending ? new ValueTask<int>(completion.Task) : new ValueTask<int>(42));
        var recovered = failed.CatchCase((NotFound error) =>
        {
            Assert.Equal(7, error.Id);
            return next;
        });

        // When
        var running = recovered.RunAsync(TestContext.Current.CancellationToken);
        if (pending) Assert.False(running.IsCompleted);
        completion.SetResult(42);

        // Then
        Assert.Equal<Result<int, AppError>>(Result.Ok(42), await running);
    }

    [Fact]
    public async Task CatchCase_InfallibleHandlerThrows_KeepsTheOriginalError()
    {
        // Given
        Effect<int, AppError, EmptyEnvironment> failed = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(7));
        var defect = new InvalidOperationException("recovery failed");
        Func<NotFound, Effect<int, Never, EmptyEnvironment>> handler = _ => throw defect;

        // When
        var exit = await failed.CatchCase(handler).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(new Failed(new NotFound(7)), Assert.Single(die.Suppressed).Case.Value);
    }
}
