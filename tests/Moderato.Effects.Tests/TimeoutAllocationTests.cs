using Moderato.Effects.Testing;

namespace Moderato.Effects.Tests;

public class TimeoutAllocationTests
{
    [Fact]
    public async Task RunAsync_ValueClock_AllocatesLikeReferenceClock()
    {
        // Given
        var value = Effect<ValueClock>.Succeed(42).Timeout(Timeout.InfiniteTimeSpan);
        var reference = Effect<DefaultEnvironment>.Succeed(42).Timeout(Timeout.InfiniteTimeSpan);
        for (var i = 0; i < 1024; i++)
        {
            await value.RunAsync(default, CancellationToken.None);
            await reference.RunAsync(DefaultEnvironment.Value, CancellationToken.None);
        }
        var valueAllocated = long.MaxValue;
        var referenceAllocated = long.MaxValue;
        var valueResult = 0;
        var referenceResult = 0;
        var completedAtOnce = true;

        // When
        for (var window = 0; window < 3; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 4096; i++)
            {
                var run = value.RunAsync(default, CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                valueResult = (await run).UnwrapOr(-1);
            }
            valueAllocated = Math.Min(valueAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 4096; i++)
            {
                var run = reference.RunAsync(DefaultEnvironment.Value, CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                referenceResult = (await run).UnwrapOr(-1);
            }
            referenceAllocated = Math.Min(referenceAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(42, valueResult);
        Assert.Equal(42, referenceResult);
        Assert.True(valueAllocated <= referenceAllocated, $"Value: {valueAllocated}; reference: {referenceAllocated}");
    }
    [Fact]
    public async Task RunExitAsync_NullClockEnvironment_DiesWithArgumentNull()
    {
        // Given
        var work = Effect<DefaultEnvironment>.Succeed(42).Timeout(Timeout.InfiniteTimeSpan);

        // When
        var exit = await work.RunExitAsync(null!, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("environment", Assert.IsType<ArgumentNullException>(die.Exception).ParamName);
    }

    [Fact]
    public async Task RunExitAsync_ValueClockGetterThrows_PreservesDefectAndSkipsBody()
    {
        // Given
        var failure = new IOException("clock");
        var started = false;
        var work = Effect<ThrowingClock>.Sync(() => { started = true; return 42; }).Timeout(Timeout.InfiniteTimeSpan);

        // When
        var exit = await work.RunExitAsync(new ThrowingClock(failure), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.False(started);
    }

    private readonly struct ThrowingClock(Exception failure) : IHas<TimeProvider>
    {
        public TimeProvider Service => throw failure;
    }
}
