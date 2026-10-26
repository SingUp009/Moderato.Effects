namespace Moderato.Effects.Tests;

public class SucceedTests
{
    [Fact]
    public async Task Succeed_WithValue_RunsToOk()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed(42);

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(42), result);
    }

    [Fact]
    public async Task Succeed_RunTwice_ReturnsTheValueComputedAtCreation()
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Succeed(++calls);

        // When
        var first = await effect.RunAsync(TestContext.Current.CancellationToken);
        var second = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(1), first);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), second);
        Assert.Equal(1, calls);
    }
}

public class FailTests
{
    [Fact]
    public async Task Fail_WithError_RunsToErr()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("boom"), result);
    }

    [Fact]
    public async Task Fail_UnionCaseWithNamedErrorType_RunsToErrOfTheUnion()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(7));

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(7), error.Value);
    }
}

public class FromResultTests
{
    [Fact]
    public async Task FromResult_Ok_RunsToOk()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.FromResult<int, string>(Result.Ok(1));

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Ok(1), result);
    }

    [Fact]
    public async Task FromResult_Err_RunsToErr()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.FromResult<int, string>(Result.Err("bad"));

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("bad"), result);
    }

    [Fact]
    public async Task ImplicitConversion_FromResult_RunsToTheSameResult()
    {
        // Given
        Result<int, string> validated = Result.Err("invalid");

        // When
        Effect<int, string, EmptyEnvironment> effect = validated;
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(validated, result);
    }

    [Fact]
    public void FromResult_DefaultResult_Throws()
    {
        // Given
        var uninitialized = default(Result<int, string>);

        // When
        Action act = () => Effect<EmptyEnvironment>.FromResult(uninitialized);

        // Then
        Assert.Throws<InvalidOperationException>(act);
    }
}

public class SyncTests
{
    [Fact]
    public async Task Sync_RunTwice_RunsTheBodyEachTime()
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Sync(() => ++calls);

        // When
        var first = await effect.RunAsync(TestContext.Current.CancellationToken);
        var second = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(1), first);
        Assert.Equal<Result<int, Never>>(Result.Ok(2), second);
    }

    [Fact]
    public void Sync_NotRun_DoesNotRunTheBody()
    {
        // Given
        var calls = 0;

        // When
        _ = Effect<EmptyEnvironment>.Sync(() => ++calls);

        // Then
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Sync_BodyThrows_RunAsyncRethrowsTheSameException()
    {
        // Given
        var bug = new InvalidOperationException("bug");
        var effect = Effect<EmptyEnvironment>.Sync<int>(() => throw bug);

        // When
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await effect.RunAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.Same(bug, thrown);
    }

    [Fact]
    public void Sync_NullBody_Throws()
    {
        // Given
        Func<int> body = null!;

        // When
        Action act = () => Effect<EmptyEnvironment>.Sync(body);

        // Then
        Assert.Throws<ArgumentNullException>("body", act);
    }
}

public class AsyncTests
{
    [Fact]
    public async Task Async_BodyCompletesAsynchronously_RunsToOk()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Async(async ct =>
        {
            await Task.Yield();
            return 42;
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(42), result);
    }

    [Fact]
    public async Task Async_Run_PassesATokenThatTheCallerCancels()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var received = CancellationToken.None;
        var effect = Effect<EmptyEnvironment>.Async(ct =>
        {
            received = ct;
            cts.Cancel();
            return ValueTask.FromResult(0);
        });

        // When
        await effect.RunAsync(cts.Token);

        // Then
        Assert.True(received.IsCancellationRequested);
    }

    [Fact]
    public async Task Async_CallerCancelsDuringTheRun_RunAsyncThrowsOperationCanceled()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var effect = Effect<EmptyEnvironment>.Async(async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        });

        // When
        var act = async () => await effect.RunAsync(cts.Token);

        // Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task Async_OperationCanceledFromAnotherToken_RunExitAsyncReturnsDie()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Async<int>(_ => throw new TaskCanceledException("HttpClient timeout"));

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<TaskCanceledException>(die.Exception);
    }

    [Fact]
    public async Task Async_CallerCancelsAndBodyThrowsAnotherException_RunExitAsyncReturnsDie()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var effect = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await cts.CancelAsync();
            throw new ObjectDisposedException("connection");
        });

        // When
        var exit = await effect.RunExitAsync(cts.Token);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<ObjectDisposedException>(die.Exception);
    }
}

public class FromResultAsyncTests
{
    [Fact]
    public async Task FromResultAsync_BodyReturnsErr_RunsToErr()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.FromResultAsync(async ct =>
        {
            await Task.Yield();
            return (Result<int, string>)Result.Err("not found");
        });

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("not found"), result);
    }

    [Fact]
    public async Task FromResultAsync_BodyReturnsDefault_RunExitAsyncReturnsDie()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.FromResultAsync(_ => ValueTask.FromResult(default(Result<int, string>)));

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
    }
}

public class TryTests
{
    [Fact]
    public async Task Try_BodySucceeds_RunsToOk()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Try(() => int.Parse("12"), (FormatException ex) => ex.Message);

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Ok(12), result);
    }

    [Fact]
    public async Task Try_BodyThrowsTheCaughtType_RunsToTheMappedErr()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Try(() => int.Parse("x"), (FormatException _) => "not a number");

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("not a number"), result);
    }

    [Fact]
    public async Task Try_BodyThrowsAnotherType_RunAsyncRethrowsTheDefect()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Try<FormatException, int, string>(() => throw new NullReferenceException(), _ => "not a number");

        // When
        var act = async () => await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        await Assert.ThrowsAsync<NullReferenceException>(act);
    }

    [Fact]
    public async Task Try_UntypedHandler_CatchesAnyException()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Try<int, string>(() => throw new NullReferenceException(), ex => ex.GetType().Name);

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err(nameof(NullReferenceException)), result);
    }

    [Fact]
    public async Task Try_HandlerThrows_RunAsyncRethrowsTheHandlersException()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Try(() => int.Parse("x"), string (FormatException _) => throw new InvalidOperationException("bad handler"));

        // When
        var act = async () => await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal("bad handler", thrown.Message);
    }

    [Fact]
    public async Task TryAsync_BodyThrowsTheCaughtType_RunsToTheMappedErr()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.TryAsync<HttpRequestException, string, string>(
            async ct =>
            {
                await Task.Yield();
                throw new HttpRequestException("503");
            },
            ex => $"unavailable: {ex.Message}");

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, string>>(Result.Err("unavailable: 503"), result);
    }

    [Fact]
    public async Task TryAsync_CallerCancels_IsNotCaughtEvenByACatchAllHandler()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var effect = Effect<EmptyEnvironment>.TryAsync<int, string>(
            async ct =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            },
            ex => ex.Message);

        // When
        var exit = await effect.RunExitAsync(cts.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }

    [Fact]
    public async Task TryAsync_OperationCanceledFromAnotherToken_IsCaughtLikeAnyException()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.TryAsync<TaskCanceledException, int, string>(
            _ => throw new TaskCanceledException("HttpClient timeout"),
            _ => "timed out");

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("timed out"), result);
    }
}

public class AccessTests
{
    [Fact]
    public async Task Access_RunWithAnEnvironment_ReadsFromIt()
    {
        // Given
        var effect = Effect<CounterEnvironment>.Access(env => env.Resolve<Counter>().Next());
        var environment = new CounterEnvironment(new Counter());

        // When
        var first = await effect.RunAsync(environment, TestContext.Current.CancellationToken);
        var second = await effect.RunAsync(environment, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(1), first);
        Assert.Equal<Result<int, Never>>(Result.Ok(2), second);
    }

    [Fact]
    public async Task AccessAsync_Run_PassesTheEnvironmentAndATokenThatTheCallerCancels()
    {
        // Given
        using var cts = new CancellationTokenSource();
        var effect = Effect<CounterEnvironment>.AccessAsync((env, ct) =>
        {
            cts.Cancel();
            return ValueTask.FromResult((env.Resolve<Counter>(), ct.IsCancellationRequested));
        });
        var counter = new Counter();

        // When
        var result = await effect.RunAsync(new CounterEnvironment(counter), cts.Token);

        // Then
        Assert.True(result.TryGetOk(out var received));
        Assert.Same(counter, received.Item1);
        Assert.True(received.Item2);
    }

    [Fact]
    public async Task Env_Run_RunsToTheEnvironment()
    {
        // Given
        var environment = new CounterEnvironment(new Counter());

        // When
        var result = await Effect<CounterEnvironment>.Env.RunAsync(environment, TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var received));
        Assert.Same(environment, received);
    }
}
