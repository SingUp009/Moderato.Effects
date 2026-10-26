namespace Moderato.Effects.Tests;

public class RunAsyncTests
{
    [Fact]
    public async Task RunAsync_DefaultEffect_Throws()
    {
        // Given
        var uninitialized = default(Effect<int, string, EmptyEnvironment>);

        // When
        var act = async () => await uninitialized.RunAsync(TestContext.Current.CancellationToken);

        // Then
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task RunAsync_TokenAlreadyCanceled_DoesNotStartTheEffect()
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Sync(() => ++calls);

        // When
        var act = async () => await effect.RunAsync(new CancellationToken(canceled: true));

        // Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunAsync_DefectAfterAnAwait_RethrowsTheSameException()
    {
        // Given
        var bug = new InvalidOperationException("bug");
        var effect = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Yield();
            throw bug;
        });

        // When
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await effect.RunAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.Same(bug, thrown);
        Assert.False(thrown.Data.Contains(Effect<int, Never, EmptyEnvironment>.SUPPRESSED_DATA_KEY));
    }
}

public class RunExitAsyncTests
{
    [Fact]
    public async Task RunExitAsync_Succeeds_ReturnsOk()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed(1);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Exit<int, Never>>(new Ok<int>(1), exit);
    }

    [Fact]
    public async Task RunExitAsync_Fails_ReturnsErr()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Exit<int, string>>(new Err<string>("boom"), exit);
    }

    [Fact]
    public async Task RunExitAsync_BodyThrows_ReturnsDieWithoutThrowing()
    {
        // Given
        var bug = new InvalidOperationException("bug");
        var effect = Effect<EmptyEnvironment>.Sync<int>(() => throw bug);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(bug, die.Exception);
        Assert.Empty(die.Suppressed);
    }

    [Fact]
    public async Task RunExitAsync_TokenAlreadyCanceled_ReturnsInterruptedWithoutRunning()
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Sync(() => ++calls);

        // When
        var exit = await effect.RunExitAsync(new CancellationToken(canceled: true));

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunExitAsync_AnyEnding_CanBeMatchedExhaustively()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("inactive"));

        // When
        var message = await effect.RunExitAsync(TestContext.Current.CancellationToken) switch
        {
            Ok<User>(var user) => $"Hello, {user.Name}",
            Err<AppError>(NotFound n) => $"no user {n.Id}",
            Err<AppError>(Forbidden f) => $"forbidden: {f.Reason}",
            Die(var exception) => $"bug: {exception.Message}",
            Interrupted => "canceled",
        };

        // Then
        Assert.Equal("forbidden: inactive", message);
    }
}

public class ImplicitConversionTests
{
    [Fact]
    public async Task ImplicitConversion_EffectThatCannotFail_IsUsableWithAnyErrorType()
    {
        // Given
        var cannotFail = Effect<EmptyEnvironment>.Succeed(1);

        // When
        Effect<int, AppError, EmptyEnvironment> effect = cannotFail;
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public async Task ImplicitConversion_EffectThatCannotSucceed_IsUsableWithAnyValueType()
    {
        // Given
        var cannotSucceed = Effect<EmptyEnvironment>.Fail("boom");

        // When
        Effect<User, string, EmptyEnvironment> effect = cannotSucceed;
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<User, string>>(Result.Err("boom"), result);
    }
}

public class EnvironmentTests
{
    [Fact]
    public async Task RunAsync_EffectForEmptyEnvironment_RunsWithoutAnEnvironment()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed("ready");

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, Never>>(Result.Ok("ready"), result);
    }

    [Fact]
    public async Task RunAsync_EffectForDefaultEnvironment_ProvidesTheSystemClock()
    {
        // Given
        var effect = Effect<DefaultEnvironment>.Access(env => env.Resolve<TimeProvider>());

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var clock));
        Assert.Same(TimeProvider.System, clock);
    }

    [Fact]
    public void Resolve_NullEnvironment_Throws()
    {
        // Given
        IHas<Counter> environment = null!;

        // When
        var act = () => environment.Resolve();

        // Then
        Assert.Throws<ArgumentNullException>("environment", act);
    }
}

public class ExitTests
{
    [Fact]
    public void Exit_Default_HasNoCase()
    {
        // Given
        var exit = default(Exit<int, string>);

        // When
        var hasValue = exit.HasValue;

        // Then
        Assert.False(hasValue);
        Assert.Null(exit.Value);
    }

    [Fact]
    public void Exit_SameOkValue_AreEqual()
    {
        // Given
        Exit<int, string> left = new Ok<int>(1);
        Exit<int, string> right = new Ok<int>(1);

        // When
        var equal = left == right;

        // Then
        Assert.True(equal);
    }
}
