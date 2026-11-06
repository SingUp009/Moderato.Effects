using System.Collections.Concurrent;

namespace Moderato.Effects.Tests;

public class PrematureStepReadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Gen_PrematureStepRead_WaitsForStepBeforeReleasing(bool acquire, bool resultProperty)
    {
        // Given
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<string>();
        var operation = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            var value = await completion.Task;
            events.Enqueue("step");
            return value;
        });
        var resource = Resource.Make(operation, (_, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            events.Enqueue("acquired release");
            return Unit.Value;
        }));
        ValueTask<int> pending = default;
        Exception? prematureFailure = null;
        var effect = Scope<Never, EmptyEnvironment>.Gen(scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() =>
            {
                events.Enqueue("defer");
                return Unit.Value;
            }));
#pragma warning disable MEFF310, MEFF306
            pending = acquire ? scope.Acquire(resource) : scope.Run(operation);
            try
            {
                if (resultProperty) _ = pending.Result;
                else _ = pending.GetAwaiter().GetResult();
            }
            catch (InvalidOperationException exception)
            {
                prematureFailure = exception;
            }
#pragma warning restore MEFF310, MEFF306
            return new ValueTask<int>(42);
        });
        string[] expected = acquire ? ["step", "acquired release", "defer"] : ["step", "defer"];

        // When
        var running = effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        var returnedBeforeCompletion = running.IsCompleted;
        completion.SetResult(7);
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var stepValue = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<InvalidOperationException>(prematureFailure);
        Assert.Equal(7, stepValue);
        Assert.Equal(42, Assert.IsType<Ok<int>>(exit.Value).Value);
        if (acquire) Assert.Contains("acquired release", events);
        Assert.Equal(expected, events.ToArray());
        Assert.False(returnedBeforeCompletion);
    }

    [Fact]
    public async Task Gen_CaughtCompletedStepDefect_RemainsHandled()
    {
        // Given
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("operation failed");
        var operation = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await completion.Task;
            throw failure;
        });
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            try
            {
                return await scope.Run(operation);
            }
            catch (InvalidOperationException exception)
            {
                Assert.Same(failure, exception);
                return 42;
            }
        });

        // When
        var running = effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        completion.SetResult();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(42, Assert.IsType<Ok<int>>(exit.Value).Value);
    }
}
