namespace Moderato.Effects.Tests;

public class CaughtExceptionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Try_MapperCancelsCaller_PreservesMappedErrorOrOriginalExceptionContext(int mode, bool rethrow)
    {
        // Given
        using var caller = new CancellationTokenSource();
        var original = new IOException("original");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapped = 0;
        string Map(IOException caught)
        {
            mapped++;
            Assert.Same(original, caught);
            caller.Cancel();
            if (rethrow) throw caught;
            return "handled";
        }
        var effect = mode == 0
            ? Effect<EmptyEnvironment>.Try<IOException, int, string>(() => throw original, Map)
            : Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(
                _ => mode == 1 ? throw original : new(completion.Task), Map);

        // When
        var running = effect.RunExitAsync(caller.Token);
        if (mode == 2)
        {
            Assert.False(running.IsCompleted);
            completion.SetException(original);
        }
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, mapped);
        if (rethrow)
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(original, die.Exception);
            var earlier = Assert.Single(die.Suppressed);
            Assert.Equal(Relation.Sequential, earlier.Relation);
            var first = Assert.IsType<Die>(earlier.Case.Value);
            Assert.Same(original, first.Exception);
            Assert.Empty(first.Suppressed);
            Assert.Equal(0, die.OmittedSuppressed);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Err<string> err));
            Assert.Equal("handled", err.Error);
        }
    }
}
