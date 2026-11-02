using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class InterruptionProvenanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Gen_RethrowsEarlierInterruptionAfterAnotherStep_KeepsInterrupted(bool interruptAgain, bool pending)
    {
        // Given
        Fiber<int, Never, EmptyEnvironment>? kept = null;
        var forever = Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await forever.Fork().Use(fiber =>
        {
            kept = fiber;
            return Effect<EmptyEnvironment>.Succeed(0);
        }).RunAsync(TestContext.Current.CancellationToken);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStep = pending
            ? Effect<EmptyEnvironment>.Async(async _ => { await complete.Task; return 0; }).AndThen(_ => kept!.Join())
            : kept!.Join();
        var effect = Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
        {
            ExceptionDispatchInfo? first = null;
            try { await scope.Run(firstStep); }
            catch (OperationCanceledException exception) { first = ExceptionDispatchInfo.Capture(exception); }

            if (interruptAgain)
            {
                for (var i = 0; i < 200; i++)
                {
                    try { await scope.Run(kept!.Join()); }
                    catch (OperationCanceledException) { }
                }
                GC.Collect();
            }
            else
            {
                await scope.Run(Effect<EmptyEnvironment>.Succeed(1));
            }

            first!.Throw();
            return 0;
        });

        // When
        var running = effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        complete.SetResult();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<Interrupted>(exit.Value);
    }
    [Fact]
    public async Task Gen_RecoversFromInterruptions_DoesNotRetainDiscardedExceptions()
    {
        // Given
        Effect<int, Never, EmptyEnvironment> interrupted = new((_, _) => new(Outcome<int, Never>.Interrupted()));
        var references = new List<WeakReference>();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            for (var i = 0; i < 500; i++)
            {
                try { await scope.Run(interrupted); }
                catch (OperationCanceledException exception) { references.Add(new WeakReference(exception)); }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return references.Count(static reference => reference.IsAlive);
        });

        // When
        var result = await work.RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var retained));
        Assert.InRange(retained, 0, 2);
    }

    [Fact]
    public async Task Gen_ThrowsUnobservedCancellationException_DoesNotMistakeItForAnInterruption()
    {
        // Given
        Effect<int, Never, EmptyEnvironment> interrupted = new((_, _) => new(Outcome<int, Never>.Interrupted()));
        var other = new OperationCanceledException();
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
        {
            for (var i = 0; i < 2; i++)
            {
                try { await scope.Run(interrupted); }
                catch (OperationCanceledException) { }
            }
            throw other;
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(other, die.Exception);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gen_RethrowsAfterMixedFailureKinds_PreservesTheOriginalKind(bool firstInterrupted)
    {
        // Given
        Effect<int, Never, EmptyEnvironment> interrupted = new((_, _) => new(Outcome<int, Never>.Interrupted()));
        var original = new IOException("original");
        var defect = Effect<EmptyEnvironment>.Sync<int>(() => throw original);
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
        {
            ExceptionDispatchInfo? first = null;
            try { await scope.Run(firstInterrupted ? interrupted : defect); }
            catch (Exception exception) when (exception is not ScopeFailure) { first = ExceptionDispatchInfo.Capture(exception); }
            for (var i = 0; i < 4; i++)
            {
                try { await scope.Run(i % 2 == 0 ? interrupted : Effect<EmptyEnvironment>.Sync<int>(() => throw new FormatException())); }
                catch (OperationCanceledException) { }
                catch (FormatException) { }
            }
            first!.Throw();
            return 0;
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        if (firstInterrupted)
        {
            Assert.IsType<Interrupted>(exit.Value);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(original, die.Exception);
        }
    }

    [Fact]
    public async Task Gen_ObservesAnInterruptionExceptionAsADefect_KeepsTheLatestObservation()
    {
        // Given
        Effect<int, Never, EmptyEnvironment> interrupted = new((_, _) => new(Outcome<int, Never>.Interrupted()));
        OperationCanceledException? original = null;
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
        {
            try { await scope.Run(interrupted); }
            catch (OperationCanceledException exception) { original = exception; }
            try { await scope.Run(Effect<EmptyEnvironment>.Sync<int>(() => throw original!)); }
            catch (OperationCanceledException) { }
            try { await scope.Run(interrupted); }
            catch (OperationCanceledException) { }
            throw original!;
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(original, die.Exception);
    }
}
