using Microsoft.Extensions.DependencyInjection;

namespace Moderato.Effects.Http.Tests;

public class LifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_IndependentInterruption_PreservesClassification(bool asynchronous)
    {
        // Given
        using var services = RequestTests.Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var child = await InterruptedChild();
        var effect = asynchronous
            ? Effect<object>.Async<Unit>(async _ => { await Task.Yield(); return Unit.Value; }).AndThen(_ => child.Join())
            : child.Join();

        // When
        var pending = request.RunAsync(effect, new object());

        // Then
        var error = await Assert.ThrowsAsync<EffectHttpException>(() => pending.AsTask());
        Assert.IsType<Interrupted>(error.Exit.Value);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task RunAsync_IndependentInterruptionAndLaterRequestCancellation_UsesFinalObservation()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = RequestTests.ServicesWithToken(cancellation.Token);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var child = await InterruptedChild();
        var resource = Resource.Make(Effect<object>.Succeed(Unit.Value), (_, _) => Effect<object>.Sync(() =>
        {
            cancellation.Cancel();
            return Unit.Value;
        }));

        // When
        var pending = request.RunAsync(resource.Use(_ => child.Join()), new object());

        // Then
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_Cancellation_WaitsForChildAndCleanup()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = RequestTests.ServicesWithToken(cancellation.Token);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var entered = Signal();
        var releasing = Signal();
        var resume = Signal();
        var released = false;
        var childEnded = false;
        var resource = Resource.Make(Effect<object>.Succeed(Unit.Value), (_, _) => Effect<object>.Async<Unit>(async token =>
        {
            Assert.False(token.CanBeCanceled);
            releasing.SetResult();
            await resume.Task;
            released = true;
            return Unit.Value;
        }));
        var effect = Scope<Never, object>.Gen(async execution =>
        {
            await execution.Fork(resource.Use(_ => Effect<object>.Async<Unit>(async token =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { childEnded = true; }
                return Unit.Value;
            })));
            await entered.Task;
            await Task.Delay(Timeout.Infinite, execution.CancellationToken);
            return Unit.Value;
        });
        try
        {
            // When
            var pending = request.RunAsync(effect, new object()).AsTask();
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await releasing.Task.WaitAsync(TestContext.Current.CancellationToken);
            var completedBeforeRelease = pending.IsCompleted;
            resume.SetResult();
            var error = await Record.ExceptionAsync(() => pending);

            // Then
            Assert.False(completedBeforeRelease);
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.True(childEnded);
            Assert.True(released);
        }
        finally { resume.TrySetResult(); }
    }

    [Fact]
    public async Task RunAsync_CancellationAfterInterruptionObserved_DoesNotReplaceException()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = RequestTests.ServicesWithToken(cancellation.Token);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var child = await InterruptedChild();

        // When
        var pending = request.RunAsync(child.Join(), new object()).AsTask();
        var error = await Record.ExceptionAsync(() => pending);
        cancellation.Cancel();
        var observedAgain = await Record.ExceptionAsync(() => pending);

        // Then
        Assert.IsType<EffectHttpException>(error);
        Assert.Same(error, observedAgain);
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Fiber<Unit, Never, object>> InterruptedChild()
    {
        Fiber<Unit, Never, object>? child = null;
        var entered = Signal();
        await Scope<Never, object>.Gen(async scope =>
        {
            child = await scope.Fork(Effect<object>.Async<Unit>(async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Unit.Value;
            }));
            await entered.Task;
            return Unit.Value;
        }).RunAsync(new object(), TestContext.Current.CancellationToken);
        return child!;
    }
}
