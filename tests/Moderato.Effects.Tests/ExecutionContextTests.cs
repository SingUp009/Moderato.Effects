namespace Moderato.Effects.Tests;

public class ExecutionContextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task AndThen_SynchronousResourceAcquisition_RestoresAmbientContextOnceBeforeMappingAndUse(int padding)
    {
        // Given
        var transitions = new List<(string? Previous, string? Current)>();
        var local = new AsyncLocal<string?>(change =>
        {
            if (change.ThreadContextChanged) transitions.Add((change.PreviousValue, change.CurrentValue));
        })
        { Value = "caller" };
        var seen = new List<string?>();
        var first = Resource.Make(Effect<EmptyEnvironment>.Sync(() =>
        {
            local.Value = "acquired";
            return 1;
        }), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var resource = first.AndThen(value =>
            {
                seen.Add(local.Value);
                local.Value = "binder";
                return Resource.Make(Effect<EmptyEnvironment>.Succeed(value + 1), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
            })
            .Map(value =>
            {
                seen.Add(local.Value);
                return value;
            });
        for (var i = 0; i < padding; i++) resource = resource.Map(static value => value);
        var effect = resource.Use(value => Effect<EmptyEnvironment>.Sync(() =>
            {
                seen.Add(local.Value);
                return value;
            }));

        // When
        var running = effect.RunAsync(CancellationToken.None);
        var observedTransitions = transitions.ToArray();
        var callerValue = local.Value;
        var result = await running;

        // Then
        Assert.True(running.IsCompletedSuccessfully);
        Assert.Equal<Result<int, Never>>(Result.Ok(2), result);
        Assert.Equal(["acquired", "caller", "caller"], seen);
        Assert.Equal(new[] { ((string?)"binder", (string?)"caller") }, observedTransitions);
        Assert.Equal("caller", callerValue);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 128)]
    [InlineData(true, 0)]
    [InlineData(true, 128)]
    public async Task RunAsync_SynchronousOperators_PreserveAmbientChangesUntilTheCallerBoundary(bool cancellable, int padding)
    {
        // Given
        var local = new AsyncLocal<string?> { Value = "caller" };
        var seen = new List<string?>();
        using var cancellation = new CancellationTokenSource();
        var effect = Effect<EmptyEnvironment>.Sync(() =>
        {
            local.Value = "source";
            return 1;
        })
            .Map(value =>
            {
                seen.Add(local.Value);
                local.Value = "map";
                return value;
            })
            .Tap(_ =>
            {
                seen.Add(local.Value);
                local.Value = "tap";
            })
            .AndThen(value =>
            {
                seen.Add(local.Value);
                local.Value = "binder";
                return Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add(local.Value);
                    local.Value = "child";
                    return value;
                });
            })
            .Map(value =>
            {
                seen.Add(local.Value);
                return value;
            });

        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunAsync(cancellable ? cancellation.Token : default);
        var callerValue = local.Value;
        var result = await running;

        // Then
        Assert.True(running.IsCompletedSuccessfully);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
        Assert.Equal(["source", "map", "tap", "binder", "child"], seen);
        Assert.Equal(cancellable ? "caller" : "child", callerValue);
        Assert.Equal(cancellable ? "caller" : "child", local.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task RunAsync_PendingOperators_RestoreEachAwaitingOperatorsAmbientContext(int padding)
    {
        // Given
        var local = new AsyncLocal<string?> { Value = "caller" };
        var seen = new List<string?>();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<EmptyEnvironment>.Async<int>(_ => new(finish.Task))
            .Map(value =>
            {
                seen.Add(local.Value);
                local.Value = "map";
                return value;
            })
            .Tap(_ =>
            {
                seen.Add(local.Value);
                local.Value = "tap";
            })
            .AndThen(value =>
            {
                seen.Add(local.Value);
                local.Value = "binder";
                return Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add(local.Value);
                    local.Value = "child";
                    return value;
                });
            })
            .Map(value =>
            {
                seen.Add(local.Value);
                return value;
            });

        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunAsync(CancellationToken.None).AsTask();
        var suspended = !running.IsCompleted;
        finish.SetResult(1);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(suspended);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
        Assert.Equal(["caller", "caller", "caller", "binder", "caller"], seen);
        Assert.Equal("caller", local.Value);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 128)]
    [InlineData(true, 0)]
    [InlineData(true, 128)]
    public async Task Provide_EnvironmentBuilderChangesAmbientContext_PreservesSynchronousAndPendingBoundaries(bool pending, int padding)
    {
        // Given
        var local = new AsyncLocal<string?> { Value = "caller" };
        var seen = new List<string?>();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = pending
            ? Effect<EmptyEnvironment>.Async<int>(async _ =>
            {
                seen.Add(local.Value);
                var value = await finish.Task.ConfigureAwait(false);
                local.Value = "source";
                return value;
            })
            : Effect<EmptyEnvironment>.Sync(() =>
            {
                seen.Add(local.Value);
                local.Value = "source";
                return 1;
            });
        var effect = source
            .Map(value =>
            {
                seen.Add(local.Value);
                local.Value = "mapped";
                return value;
            })
            .Provide<EmptyEnvironment>(environment =>
            {
                seen.Add(local.Value);
                local.Value = "provided";
                return environment;
            })
            .Map(value =>
            {
                seen.Add(local.Value);
                return value;
            });

        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunAsync(CancellationToken.None).AsTask();
        var completed = running.IsCompletedSuccessfully;
        var callerValue = local.Value;
        finish.SetResult(1);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
        Assert.Equal(pending ? ["caller", "provided", "provided", "provided"] : new[] { "caller", "provided", "source", "mapped" }, seen);
        Assert.Equal(pending ? "provided" : "mapped", callerValue);
        Assert.Equal(pending ? "provided" : "mapped", local.Value);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 128)]
    [InlineData(true, 0)]
    [InlineData(true, 128)]
    public async Task Use_AcquisitionChangesAmbientContext_PreservesUseAndReleaseBoundaries(bool pending, int padding)
    {
        // Given
        var local = new AsyncLocal<string?> { Value = "caller" };
        var seen = new List<string?>();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = pending
            ? Effect<EmptyEnvironment>.Async<int>(_ => new(finish.Task))
            : Effect<EmptyEnvironment>.Sync(() =>
            {
                local.Value = "source";
                return 1;
            });
        var resource = Resource.Make(source, (_, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                seen.Add(local.Value);
                return Unit.Value;
            }))
            .Map(value =>
            {
                seen.Add(local.Value);
                local.Value = "mapped";
                return value;
            });
        for (var i = 0; i < padding; i++) resource = resource.Map(static value => value);
        var effect = resource.Use(value => Effect<EmptyEnvironment>.Sync(() =>
            {
                seen.Add(local.Value);
                local.Value = "used";
                return value;
            }))
            .Map(value =>
            {
                seen.Add(local.Value);
                return value;
            });

        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunAsync(CancellationToken.None).AsTask();
        var completed = running.IsCompletedSuccessfully;
        var callerValue = local.Value;
        finish.SetResult(1);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
        Assert.Equal(pending ? ["caller", "caller", "used", "caller"] : new[] { "source", "mapped", "used", "caller" }, seen);
        Assert.Equal("caller", callerValue);
        Assert.Equal("caller", local.Value);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 128)]
    [InlineData(true, 0)]
    [InlineData(true, 128)]
    public async Task Gen_StepChangesAmbientContext_PreservesBlockAndReleaseBoundaries(bool pending, int padding)
    {
        // Given
        var local = new AsyncLocal<string?> { Value = "caller" };
        var seen = new List<string?>();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = pending
            ? Effect<EmptyEnvironment>.Async<int>(_ => new(finish.Task))
            : Effect<EmptyEnvironment>.Sync(() =>
            {
                local.Value = "source";
                return 1;
            });
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
            {
                scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add(local.Value);
                    return Unit.Value;
                }));
                local.Value = "block";
                var value = await scope.Run(source.Map(value =>
                {
                    seen.Add(local.Value);
                    local.Value = "mapped";
                    return value;
                })).ConfigureAwait(false);
                seen.Add(local.Value);
                local.Value = "ended";
                return value;
            })
            .Map(value =>
            {
                seen.Add(local.Value);
                return value;
            });

        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunAsync(CancellationToken.None).AsTask();
        var completed = running.IsCompletedSuccessfully;
        var callerValue = local.Value;
        finish.SetResult(1);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
        Assert.Equal(pending ? ["block", "block", "caller", "caller"] : new[] { "source", "mapped", "caller", "caller" }, seen);
        Assert.Equal("caller", callerValue);
        Assert.Equal("caller", local.Value);
    }

    [Theory]
    [InlineData("Operators", false, 0)]
    [InlineData("Operators", false, 128)]
    [InlineData("Operators", true, 0)]
    [InlineData("Operators", true, 128)]
    [InlineData("Provide", false, 0)]
    [InlineData("Provide", false, 128)]
    [InlineData("Provide", true, 0)]
    [InlineData("Provide", true, 128)]
    [InlineData("Resource", false, 0)]
    [InlineData("Resource", false, 128)]
    [InlineData("Resource", true, 0)]
    [InlineData("Resource", true, 128)]
    [InlineData("Gen", false, 0)]
    [InlineData("Gen", false, 128)]
    [InlineData("Gen", true, 0)]
    [InlineData("Gen", true, 128)]
    public async Task RunAsync_WithSynchronizationContext_OnlySynchronousCallbacksRunOnIt(string composition, bool pending, int padding)
    {
        // Given
        var context = new RecordingSynchronizationContext();
        var seen = new List<SynchronizationContext?>();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = pending ? Effect<EmptyEnvironment>.Async<int>(_ => new(finish.Task)) : Effect<EmptyEnvironment>.Succeed(1);
        var effect = composition switch
        {
            "Operators" => source.Map(value =>
            {
                seen.Add(SynchronizationContext.Current);
                return value;
            })
                .Tap(_ => seen.Add(SynchronizationContext.Current))
                .AndThen(value => Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add(SynchronizationContext.Current);
                    return value;
                })),
            "Provide" => source.Provide<EmptyEnvironment>(static environment => environment)
                .Map(value =>
                {
                    seen.Add(SynchronizationContext.Current);
                    return value;
                }),
            "Resource" => Resource.Make(source, (_, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                seen.Add(SynchronizationContext.Current);
                return Unit.Value;
            }))
                .Use(value => Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add(SynchronizationContext.Current);
                    return value;
                })),
            _ => Scope<Never, EmptyEnvironment>.Gen(async scope =>
            {
                var value = await scope.Run(source).ConfigureAwait(false);
                seen.Add(SynchronizationContext.Current);
                return value;
            })
        };
        var previous = SynchronizationContext.Current;
        Task<Result<int, Never>> running;
        bool completed;

        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            running = effect.RunAsync(CancellationToken.None).AsTask();
            completed = running.IsCompletedSuccessfully;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        finish.SetResult(1);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal<Result<int, Never>>(Result.Ok(1), result);
        Assert.NotEmpty(seen);
        Assert.All(seen, current => Assert.Same(pending ? null : context, current));
        Assert.Equal(0, context.Posts);
        Assert.Same(previous, SynchronizationContext.Current);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int posts;

        internal int Posts => Volatile.Read(ref posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
