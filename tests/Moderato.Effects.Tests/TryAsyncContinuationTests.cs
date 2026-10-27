namespace Moderato.Effects.Tests;

public class TryAsyncContinuationTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task TryAsync_BodyCompletes_PreservesResultAndExceptionContext(int kind, bool pending)
    {
        // Given
        Exception failure = kind switch
        {
            2 => new InvalidOperationException("unmatched"),
            4 => new ScopeFailure(new object(), "original"),
            _ => new IOException("original"),
        };
        var mappingFailure = new ArgumentException("mapper");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending)
        {
            if (kind == 0) completion.SetResult(42);
            else completion.SetException(failure);
        }
        var calls = 0;
        var mapped = 0;
        ValueTask<int> Body(CancellationToken _)
        {
            calls++;
            return new(completion.Task);
        }
        string Map(Exception caught)
        {
            mapped++;
            Assert.Same(failure, caught);
            if (kind == 3) throw mappingFailure;
            return "mapped";
        }
        var effect = kind == 2
            ? Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(Body, Map)
            : Effect<EmptyEnvironment>.TryAsync<int, string>(Body, Map);
        Assert.Equal(0, calls);
        Assert.Equal(0, mapped);

        // When
        // None keeps caller isolation from masking the body exception boundary.
        var running = effect.RunExitAsync(CancellationToken.None);
        if (pending)
        {
            Assert.False(running.IsCompleted);
            if (kind == 0) completion.SetResult(42);
            else completion.SetException(failure);
        }
        var first = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = await effect.RunExitAsync(CancellationToken.None);

        // Then
        Assert.Equal(2, calls);
        Assert.Equal(kind is 1 or 3 ? 2 : 0, mapped);
        foreach (var exit in new[] { first, second })
        {
            if (kind == 0)
            {
                Assert.True(exit.TryGetValue(out Ok<int> ok));
                Assert.Equal(42, ok.Value);
            }
            else if (kind == 1)
            {
                Assert.True(exit.TryGetValue(out Err<string> err));
                Assert.Equal("mapped", err.Error);
            }
            else
            {
                Assert.True(exit.TryGetValue(out Die? die));
                Assert.Same(kind == 3 ? mappingFailure : failure, die.Exception);
                if (kind == 3)
                {
                    var suppressed = Assert.Single(die.Suppressed);
                    Assert.Equal(Relation.Sequential, suppressed.Relation);
                    Assert.Same(failure, Assert.IsType<Die>(suppressed.Case.Value).Exception);
                }
                else
                {
                    Assert.Empty(die.Suppressed);
                }
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TryAsync_BodyThrowsCancellation_MapsOnlyWhenCallerIsNotCanceled(bool pending, bool cancel)
    {
        // Given
        using var caller = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new OperationCanceledException();
        var mapped = 0;
        var effect = Effect<EmptyEnvironment>.TryAsync<int, string>(_ =>
        {
            if (pending) return new(completion.Task);
            if (cancel) caller.Cancel();
            throw failure;
        }, caught =>
        {
            mapped++;
            Assert.Same(failure, caught);
            return "mapped";
        });

        // When
        var running = effect.RunExitAsync(caller.Token);
        if (pending)
        {
            Assert.False(running.IsCompleted);
            if (cancel) caller.Cancel();
            completion.SetException(failure);
        }
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(cancel ? 0 : 1, mapped);
        if (cancel)
        {
            Assert.True(exit.TryGetValue(out Interrupted? _));
        }
        else
        {
            Assert.True(exit.TryGetValue(out Err<string> err));
            Assert.Equal("mapped", err.Error);
        }
    }
}
