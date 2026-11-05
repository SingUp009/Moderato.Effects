using System.Runtime.CompilerServices;

namespace Moderato.Effects.Tests;

[CollectionDefinition("Global effect tracker", DisableParallelization = true)]
public sealed class GlobalEffectTrackerCollection;

[Collection("Global effect tracker")]
public sealed class EffectTrackerTests : IDisposable
{
    private readonly bool wasEnabled = EffectTracker.EnableTracking;
    private readonly bool capturedStacks = EffectTracker.EnableStackTrace;

    public EffectTrackerTests()
    {
        EffectTracker.EnableTracking = false;
        EffectTracker.EnableStackTrace = false;
    }

    [Fact]
    public async Task GetSnapshot_Disabled_DoesNotTrackExecutions()
    {
        // Given
        EffectTracker.EnableStackTrace = true;
        var effect = Scope<Never, EmptyEnvironment>.Gen(_ =>
            new ValueTask<EffectTrackingState[]>(EffectTracker.GetSnapshot()));

        // When
        var result = await effect.RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var snapshot));
        Assert.Empty(snapshot);
        Assert.False(EffectTracker.EnableTracking);
    }

    [Fact]
    public async Task GetSnapshot_ActiveLifetimes_RemovesEachOnlyAfterCompletion()
    {
        // Given
        EffectTracker.EnableTracking = true;
        var entered = Signal();
        var endBody = Signal();
        var releasing = Signal();
        var endRelease = Signal();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new object()), (_, _) =>
            Effect<EmptyEnvironment>.Async<Unit>(async token =>
            {
                Assert.False(token.CanBeCanceled);
                releasing.TrySetResult();
                await endRelease.Task.ConfigureAwait(false);
                return Unit.Value;
            }));
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(resource);
            await scope.Fork(Effect<EmptyEnvironment>.Async<Unit>(async token =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Unit.Value;
            }));
            entered.TrySetResult();
            await endBody.Task.ConfigureAwait(false);
            return Unit.Value;
        });

        // When
        var running = effect.RunExitAsync(CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var active = EffectTracker.GetSnapshot();
            endBody.TrySetResult();
            await releasing.Task.WaitAsync(TestContext.Current.CancellationToken);
            var duringRelease = EffectTracker.GetSnapshot();
            endRelease.TrySetResult();
            var result = await running.WaitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal(3, active.Length);
            Assert.Single(active, state => state.Kind == EffectTrackingKind.Scope);
            Assert.Single(active, state => state.Kind == EffectTrackingKind.Fiber);
            Assert.Single(active, state => state.Kind == EffectTrackingKind.Resource);
            Assert.Equal(2, duringRelease.Length);
            Assert.DoesNotContain(duringRelease, state => state.Kind == EffectTrackingKind.Fiber);
            Assert.Equal(3, active.Select(state => state.Id).Distinct().Count());
            Assert.All(active, state => Assert.False(string.IsNullOrWhiteSpace(state.TypeName)));
            Assert.True(result.TryGetValue(out Ok<Unit> _));
            Assert.Empty(EffectTracker.GetSnapshot());
        }
        finally
        {
            endBody.TrySetResult();
            endRelease.TrySetResult();
            await running.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSnapshot_StackCapture_ReturnsConfiguredMetadata(bool captureStack)
    {
        // Given
        EffectTracker.EnableTracking = true;
        EffectTracker.EnableStackTrace = captureStack;
        var before = DateTimeOffset.UtcNow;
        var effect = Scope<Never, EmptyEnvironment>.Gen(_ =>
            new ValueTask<EffectTrackingState[]>(EffectTracker.GetSnapshot()));

        // When
        var result = await effect.RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var snapshot));
        var state = Assert.Single(snapshot);
        Assert.Equal(EffectTrackingKind.Scope, state.Kind);
        Assert.InRange(state.StartedAt, before, DateTimeOffset.UtcNow);
        if (captureStack) Assert.False(string.IsNullOrWhiteSpace(state.StackTrace));
        else Assert.Null(state.StackTrace);
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Fact]
    public async Task GetSnapshot_JoinedFiberCompletes_RemovesItBeforeParentScopeEnds()
    {
        // Given
        EffectTracker.EnableTracking = true;
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var child = await scope.Fork(Effect<EmptyEnvironment>.Succeed(42));
            await scope.Run(child.Join());
            return EffectTracker.GetSnapshot();
        });

        // When
        var result = await effect.RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var snapshot));
        Assert.Equal(EffectTrackingKind.Scope, Assert.Single(snapshot).Kind);
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("err")]
    [InlineData("die")]
    [InlineData("interrupted")]
    public async Task GetSnapshot_CompletedScope_RemovesEveryOutcome(string ending)
    {
        // Given
        EffectTracker.EnableTracking = true;
        using var cancellation = new CancellationTokenSource();
        var effect = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            await Task.Yield();
            if (ending == "err") throw scope.Fail("expected");
            if (ending == "die") throw new IOException("defect");
            if (ending == "interrupted")
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, scope.CancellationToken).ConfigureAwait(false);
            }
            return 42;
        });

        // When
        var result = await effect.RunExitAsync(cancellation.Token);

        // Then
        Assert.True(ending switch
        {
            "ok" => result.TryGetValue(out Ok<int> _),
            "err" => result.TryGetValue(out Err<string> _),
            "die" => result.TryGetValue(out Die? _),
            _ => result.TryGetValue(out Interrupted? _),
        });
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Fact]
    public async Task GetSnapshot_AcquisitionFails_DoesNotTrackUnacquiredResource()
    {
        // Given
        EffectTracker.EnableTracking = true;
        EffectTrackingState[] duringAcquisition = [];
        var released = false;
        var defect = new IOException("acquisition");
        var resource = Resource.Make(Effect<EmptyEnvironment>.Sync<int>(() =>
        {
            duringAcquisition = EffectTracker.GetSnapshot();
            throw defect;
        }), (_, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            released = true;
            return Unit.Value;
        }));

        // When
        var result = await resource.Use(Effect<EmptyEnvironment>.Succeed).RunExitAsync(CancellationToken.None);

        // Then
        Assert.Single(duringAcquisition);
        Assert.DoesNotContain(duringAcquisition, state => state.Kind == EffectTrackingKind.Resource);
        Assert.True(result.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.False(released);
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Fact]
    public async Task GetSnapshot_ReleaseFails_RemovesResourceAndScope()
    {
        // Given
        EffectTracker.EnableTracking = true;
        var defect = new IOException("release");
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) =>
            Effect<EmptyEnvironment>.Sync<Unit>(() => throw defect));

        // When
        var result = await resource.Use(Effect<EmptyEnvironment>.Succeed).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Fact]
    public async Task EnableTracking_DisabledAndReenabled_DoesNotRetroactivelyTrackOldScope()
    {
        // Given
        EffectTracker.EnableTracking = true;
        var oldStarted = Signal();
        var oldResume = Signal();
        var newStarted = Signal();
        var newResume = Signal();
        var oldEffect = Scope<Never, EmptyEnvironment>.Gen(async _ =>
        {
            oldStarted.TrySetResult();
            await oldResume.Task.ConfigureAwait(false);
            return Unit.Value;
        });
        var newEffect = Scope<Never, EmptyEnvironment>.Gen(async _ =>
        {
            newStarted.TrySetResult();
            await newResume.Task.ConfigureAwait(false);
            return Unit.Value;
        });
        var oldRun = oldEffect.RunAsync(CancellationToken.None).AsTask();
        await oldStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var oldId = Assert.Single(EffectTracker.GetSnapshot()).Id;
        EffectTracker.EnableTracking = true;
        Assert.Equal(oldId, Assert.Single(EffectTracker.GetSnapshot()).Id);

        // When
        EffectTracker.EnableTracking = false;
        Assert.Empty(EffectTracker.GetSnapshot());
        EffectTracker.EnableTracking = true;
        Assert.Empty(EffectTracker.GetSnapshot());
        var newRun = newEffect.RunAsync(CancellationToken.None).AsTask();
        try
        {
            await newStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            oldResume.TrySetResult();
            await oldRun.WaitAsync(TestContext.Current.CancellationToken);
            var remaining = EffectTracker.GetSnapshot();

            // Then
            Assert.True(Assert.Single(remaining).Id > oldId);
        }
        finally
        {
            oldResume.TrySetResult();
            newResume.TrySetResult();
            await Task.WhenAll(oldRun, newRun).WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Fact]
    public async Task GetSnapshot_ConcurrentExecutions_ReturnsDistinctOrderedLifetimes()
    {
        // Given
        EffectTracker.EnableTracking = true;
        const int RUNS = 32;
        var started = 0;
        var allStarted = Signal();
        var resume = Signal();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var effect = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(resource);
            if (Interlocked.Increment(ref started) == RUNS) allStarted.TrySetResult();
            await resume.Task.ConfigureAwait(false);
            return Unit.Value;
        });

        // When
        var running = Enumerable.Range(0, RUNS).Select(_ => Task.Run(async () =>
            await effect.RunExitAsync(CancellationToken.None))).ToArray();
        try
        {
            await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var snapshot = EffectTracker.GetSnapshot();

            // Then
            Assert.Equal(RUNS * 2, snapshot.Length);
            Assert.Equal(RUNS * 2, snapshot.Select(state => state.Id).Distinct().Count());
            Assert.Equal(snapshot.Select(state => state.Id).Order(), snapshot.Select(state => state.Id));
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(running).WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Empty(EffectTracker.GetSnapshot());
    }

    [Fact]
    public async Task GetSnapshot_KeptAfterCompletion_DoesNotRetainEnvironmentOrResource()
    {
        // Given
        EffectTracker.EnableTracking = true;
        EffectTracker.EnableStackTrace = true;

        // When
        var observed = await RunAndCaptureAsync();
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Then
        Assert.False(observed.Environment.TryGetTarget(out _));
        Assert.False(observed.Resource.TryGetTarget(out _));
        Assert.NotEmpty(observed.Snapshot);
        Assert.Empty(EffectTracker.GetSnapshot());
        GC.KeepAlive(observed.Snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<object> Environment, WeakReference<object> Resource, EffectTrackingState[] Snapshot)> RunAndCaptureAsync()
    {
        var environment = new object();
        var environmentReference = new WeakReference<object>(environment);
        WeakReference<object>? resourceReference = null;
        EffectTrackingState[] snapshot = [];
        var resource = Resource.Make(Effect<object>.Sync(static () => new object()), static (_, _) => Effect<object>.Succeed(Unit.Value));
        var effect = Scope<Never, object>.Gen(async scope =>
        {
            var value = await scope.Acquire(resource);
            resourceReference = new WeakReference<object>(value);
            snapshot = EffectTracker.GetSnapshot();
            return Unit.Value;
        });
        await effect.RunAsync(environment, CancellationToken.None);
        return (environmentReference, resourceReference!, snapshot);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        EffectTracker.EnableTracking = false;
        EffectTracker.EnableStackTrace = capturedStacks;
        EffectTracker.EnableTracking = wasEnabled;
    }
}
