namespace Moderato.Effects.Tests;

public class WidenEnvTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WidenEnv_Constant_RetainsResultAcrossRuns(bool failure)
    {
        // Given
        Result<int, string> expected = failure ? Result.Err("error") : Result.Ok(42);
        var source = Effect<object>.FromResult(expected);

        // When
        var effect = source.WidenEnv<string>();
        var first = await effect.RunAsync("first", CancellationToken.None);
        var second = await effect.RunAsync("second", CancellationToken.None);

        // Then
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WidenEnv_ConstantWithCanceledToken_Interrupts(bool failure)
    {
        // Given
        Effect<int, string, object> source = failure ? Effect<object>.Fail("error") : Effect<object>.Succeed(42);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // When
        var effect = source.WidenEnv<string>();
        var act = async () => await effect.RunAsync("environment", cancellation.Token);
        var exit = await effect.RunExitAsync("environment", cancellation.Token);

        // Then
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public void WidenEnv_Default_RejectsUninitializedEffect()
    {
        // Given
        Effect<int, string, object> source = default;

        // When
        Action act = () => { _ = source.WidenEnv<string>(); };

        // Then
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public async Task WidenEnv_Deferred_RemainsLazyAndUsesEachEnvironment()
    {
        // Given
        var calls = 0;
        var source = Effect<object>.Access(env => { calls++; return env; });

        // When
        var effect = source.WidenEnv<string>();
        var callsAfterConstruction = calls;
        var first = await effect.RunAsync("first", CancellationToken.None);
        var second = await effect.RunAsync("second", CancellationToken.None);

        // Then
        Assert.Equal(0, callsAfterConstruction);
        Assert.Equal(2, calls);
        Assert.Equal<Result<object, Never>>(Result.Ok<object>("first"), first);
        Assert.Equal<Result<object, Never>>(Result.Ok<object>("second"), second);
    }
}
