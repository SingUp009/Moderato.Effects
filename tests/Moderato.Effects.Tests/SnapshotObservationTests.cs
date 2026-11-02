namespace Moderato.Effects.Tests;

public class SnapshotObservationTests
{
    [Theory]
    [InlineData("Run", "None", false)]
    [InlineData("Run", "None", true)]
    [InlineData("Run", "AsTask", false)]
    [InlineData("Run", "AsTask", true)]
    [InlineData("Run", "Preserve", false)]
    [InlineData("Run", "Preserve", true)]
    [InlineData("Acquire", "None", false)]
    [InlineData("Acquire", "None", true)]
    [InlineData("Acquire", "AsTask", false)]
    [InlineData("Acquire", "AsTask", true)]
    [InlineData("Acquire", "Preserve", false)]
    [InlineData("Acquire", "Preserve", true)]
    public async Task Gen_AdapterReadsDefectAfterBodySnapshot_PreservesFailure(string method, string adapter, bool bodyDies)
    {
        // Given
        var completion = new TaskCompletionSource<Result<int, string>>();
        var lateFailure = new InvalidOperationException("late defect");
        var bodyFailure = new IOException("body defect");
        Task<int>? abandoned = null;
        CancellationTokenRegistration registration = default;
        ExitCase? releaseExit = null;
        var operation = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(completion.Task));
        var resource = Resource.Make(operation, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var effect = Scope<string, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(exit => Effect<EmptyEnvironment>.Sync(() =>
            {
                releaseExit = exit;
                registration.Dispose();
                return Unit.Value;
            }));
#pragma warning disable MEFF310
            var step = method == "Acquire" ? scope.Acquire(resource) : scope.Run(operation);
#pragma warning restore MEFF310
            abandoned = adapter switch
            {
                "AsTask" => step.AsTask(),
                "Preserve" => step.Preserve().AsTask(),
                _ => null,
            };
            registration = scope.CancellationToken.Register(() =>
            {
                completion.SetException(lateFailure);
                if (abandoned is null) return;
#pragma warning disable MEFF306
                try { abandoned.GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { }
#pragma warning restore MEFF306
            });
            if (bodyDies) throw bodyFailure;
            return new(42);
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (adapter != "None") Assert.True(abandoned!.IsFaulted);
        var die = Assert.IsType<Die>(exit.Value);
        var releaseDie = Assert.IsType<Die>(releaseExit!.Value.Value);
        Assert.Same(bodyDies ? bodyFailure : lateFailure, die.Exception);
        Assert.Same(die.Exception, releaseDie.Exception);
        Assert.Equal(0, die.OmittedSuppressed);
        if (bodyDies)
        {
            var context = Assert.Single(die.Suppressed);
            Assert.Equal(Relation.Sequential, context.Relation);
            Assert.Same(lateFailure, Assert.IsType<Die>(context.Case.Value).Exception);
            Assert.Same(lateFailure, Assert.IsType<Die>(Assert.Single(releaseDie.Suppressed).Case.Value).Exception);
        }
        else
        {
            Assert.Empty(die.Suppressed);
            Assert.Empty(releaseDie.Suppressed);
        }
    }

    [Theory]
    [InlineData("Run", "None", false)]
    [InlineData("Run", "None", true)]
    [InlineData("Run", "AsTask", false)]
    [InlineData("Run", "AsTask", true)]
    [InlineData("Run", "Preserve", false)]
    [InlineData("Run", "Preserve", true)]
    [InlineData("Acquire", "None", false)]
    [InlineData("Acquire", "None", true)]
    [InlineData("Acquire", "AsTask", false)]
    [InlineData("Acquire", "AsTask", true)]
    [InlineData("Acquire", "Preserve", false)]
    [InlineData("Acquire", "Preserve", true)]
    public async Task Gen_HandlesPreviouslyQueuedStepDefectBeforeSnapshot_DoesNotResurrectFailure(string method, string adapter, bool bodyDies)
    {
        // Given
        var firstCompletion = new TaskCompletionSource<Result<int, string>>();
        var secondCompletion = new TaskCompletionSource<Result<int, string>>();
        var handledFailure = new InvalidOperationException("handled defect");
        var bodyFailure = new IOException("body defect");
        var firstOperation = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(firstCompletion.Task));
        var secondOperation = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(secondCompletion.Task));
        var firstResource = Resource.Make(firstOperation, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var releases = 0;
        var secondResource = Resource.Make(secondOperation, (_, _) => Effect<EmptyEnvironment>.Sync(() => { releases++; return Unit.Value; }));
        var effect = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
#pragma warning disable MEFF310
            var first = method == "Acquire" ? scope.Acquire(firstResource) : scope.Run(firstOperation);
            firstCompletion.SetException(handledFailure);
            // Checking status leaves the completed step unobserved until the body reads it.
            Assert.True(first.IsCompleted);
            var second = method == "Acquire" ? scope.Acquire(secondResource) : scope.Run(secondOperation);
#pragma warning restore MEFF310
            try
            {
                if (adapter == "AsTask") await first.AsTask();
                else if (adapter == "Preserve") await first.Preserve();
                else await first;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Same(handledFailure, exception);
            }
            secondCompletion.SetResult(new Ok<int>(2));
            Assert.Equal(2, await second);
            if (bodyDies) throw bodyFailure;
            return 42;
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(method == "Acquire" ? 1 : 0, releases);
        if (bodyDies)
        {
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Same(bodyFailure, die.Exception);
            Assert.Empty(die.Suppressed);
        }
        else
        {
            Assert.Equal(42, Assert.IsType<Ok<int>>(exit.Value).Value);
        }
    }
    [Theory]
    [InlineData("Run", "None", false)]
    [InlineData("Run", "None", true)]
    [InlineData("Run", "AsTask", false)]
    [InlineData("Run", "AsTask", true)]
    [InlineData("Run", "Preserve", false)]
    [InlineData("Run", "Preserve", true)]
    [InlineData("Acquire", "None", false)]
    [InlineData("Acquire", "None", true)]
    [InlineData("Acquire", "AsTask", false)]
    [InlineData("Acquire", "AsTask", true)]
    [InlineData("Acquire", "Preserve", false)]
    [InlineData("Acquire", "Preserve", true)]
    public async Task Gen_QueuedStepAdapterReadsAfterSnapshot_PreservesFailure(string method, string adapter, bool bodyDies)
    {
        // Given
        var firstCompletion = new TaskCompletionSource<Result<int, string>>();
        var secondCompletion = new TaskCompletionSource<Result<int, string>>();
        var lateFailure = new InvalidOperationException("queued defect");
        var bodyFailure = new IOException("body defect");
        var firstOperation = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(firstCompletion.Task));
        var secondOperation = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(secondCompletion.Task));
        var firstResource = Resource.Make(firstOperation, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var releases = 0;
        var secondResource = Resource.Make(secondOperation, (_, _) => Effect<EmptyEnvironment>.Sync(() => { releases++; return Unit.Value; }));
        Task<int>? abandoned = null;
        CancellationTokenRegistration registration = default;
        ExitCase? releaseExit = null;
        var effect = Scope<string, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(exit => Effect<EmptyEnvironment>.Sync(() =>
            {
                releaseExit = exit;
                registration.Dispose();
                return Unit.Value;
            }));
#pragma warning disable MEFF310
            var first = method == "Acquire" ? scope.Acquire(firstResource) : scope.Run(firstOperation);
            firstCompletion.SetException(lateFailure);
            // Checking status leaves the completed step unobserved until the adapter reads it.
            Assert.True(first.IsCompleted);
            _ = method == "Acquire" ? scope.Acquire(secondResource) : scope.Run(secondOperation);
#pragma warning restore MEFF310
            registration = scope.CancellationToken.Register(() =>
            {
                abandoned = adapter switch
                {
                    "AsTask" => first.AsTask(),
                    "Preserve" => first.Preserve().AsTask(),
                    _ => null,
                };
#pragma warning disable MEFF306
                try { abandoned?.GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { }
#pragma warning restore MEFF306
                secondCompletion.SetResult(new Ok<int>(2));
            });
            if (bodyDies) throw bodyFailure;
            return new(42);
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (adapter != "None") Assert.True(abandoned!.IsFaulted);
        Assert.Equal(method == "Acquire" ? 1 : 0, releases);
        var die = Assert.IsType<Die>(exit.Value);
        var releaseDie = Assert.IsType<Die>(releaseExit!.Value.Value);
        Assert.Same(bodyDies ? bodyFailure : lateFailure, die.Exception);
        Assert.Same(die.Exception, releaseDie.Exception);
        if (bodyDies)
        {
            Assert.Same(lateFailure, Assert.IsType<Die>(Assert.Single(die.Suppressed).Case.Value).Exception);
            Assert.Same(lateFailure, Assert.IsType<Die>(Assert.Single(releaseDie.Suppressed).Case.Value).Exception);
        }
        else
        {
            Assert.Empty(die.Suppressed);
            Assert.Empty(releaseDie.Suppressed);
        }
    }

}
