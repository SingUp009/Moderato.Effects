namespace Moderato.Effects.Tests;

public class IndependentErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_IndependentErrorWaitsForCanceledSibling_KeepsTheError(bool nested)
    {
        // Given
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await Task.WhenAll(started.Task, otherStarted.Task);
            return Result.Err("a");
        });
        Effect<int, string, EmptyEnvironment> b = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                canceled.SetResult();
                await finish.Task;
                throw;
            }
            return 0;
        });
        var c = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            _ = ct.Register(() => finish.TrySetResult());
            otherStarted.SetResult();
            await canceled.Task;
            return Result.Err("c");
        });
        var inner = Effect.All([a, b], Concurrency.Parallel).Map(static values => values.Length);
        var work = nested ? Effect.All([inner, c], Concurrency.Parallel) : Effect.All([a, b, c], Concurrency.Parallel);

        // When
        var exit = await work.RunExitAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<string> error));
        Assert.Equal("a", error.Error);
    }

    [Theory]
    [InlineData("all", false)]
    [InlineData("race", false)]
    [InlineData("raceFirst", false)]
    [InlineData("allErrors", false)]
    [InlineData("allSettled", false)]
    [InlineData("all", true)]
    [InlineData("allErrors", true)]
    [InlineData("allSettled", true)]
    public async Task Group_CallerCancelsWhileAnIndependentErrorReleases_PreservesItsContract(string operation, bool turnsIntoError)
    {
        // Given
        using var caller = new CancellationTokenSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> failure = Effect<EmptyEnvironment>.Fail("early");
        Effect<Unit, string, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        var first = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Async(async _ =>
        {
            held.SetResult();
            await finish.Task;
            return Unit.Value;
        })).Use(_ => failure);
        var second = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (turnsIntoError)
            {
                return Result.Err("canceled");
            }
            return Result.Ok(0);
        });
        Exit<int, string>[]? settled = null;
        var work = operation switch
        {
            "all" => Effect.All([first, second], Concurrency.Parallel).Map(static values => values.Length),
            "race" => Effect.Race(first, second),
            "raceFirst" => Effect.RaceFirst(first, second),
            "allErrors" => Effect.AllErrors([first, second], Concurrency.Parallel).Map(static values => values.Length).MapErr(static errors => errors[0]),
            _ => Effect.AllSettled([first, second], Concurrency.Parallel).Tap(values => settled = values).Map(static values => values.Length),
        };

        // When
        var running = work.RunExitAsync(caller.Token).AsTask();
        await Task.WhenAll(held.Task, started.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        caller.Cancel();
        finish.SetResult();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (operation == "allSettled" && turnsIntoError)
        {
            Assert.True(exit.TryGetValue(out Ok<int> ok));
            Assert.Equal(2, ok.Value);
            Assert.NotNull(settled);
            Assert.Equal<Exit<int, string>>(new Err<string>("early"), settled[0]);
            Assert.Equal<Exit<int, string>>(new Err<string>("canceled"), settled[1]);
        }
        else if (operation is "allErrors" or "allSettled")
        {
            Assert.True(exit.TryGetValue(out Interrupted? _));
        }
        else
        {
            Assert.True(exit.TryGetValue(out Err<string> error));
            Assert.Equal("early", error.Error);
        }
    }

    [Fact]
    public async Task AllErrors_CompleteValidationReleasesAfterCancellation_ReturnsIndependentErrors()
    {
        // Given
        using var caller = new CancellationTokenSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, EmptyEnvironment> failure = Effect<EmptyEnvironment>.Fail("early");
        Effect<Unit, string, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        var first = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Async(async _ =>
        {
            held.SetResult();
            await finish.Task;
            return Unit.Value;
        })).Use(_ => failure);

        // When
        var running = Effect.AllErrors([first], Concurrency.Parallel).RunExitAsync(caller.Token).AsTask();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        caller.Cancel();
        finish.SetResult();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<string[]> errors));
        Assert.Equal(["early"], errors.Error);
    }
}
