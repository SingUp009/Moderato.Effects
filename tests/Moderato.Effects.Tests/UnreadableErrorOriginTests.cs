using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

public class UnreadableErrorOriginTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Timeout_UnreadableError_KeepsItsCancellationClassification(bool independent, bool withRelease)
    {
        // Given
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new BrokenError();
        ExitCase? released = null;
        var work = Scope<BrokenError, TimeEnvironment>.Gen<int>(async scope =>
        {
            if (withRelease) scope.Defer(exit => Effect<TimeEnvironment>.Sync(() => { released = exit; return Unit.Value; }));
            if (independent)
            {
                try { throw scope.Fail(error); }
                catch (ScopeFailure)
                {
                    started.SetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken); }
                    catch (OperationCanceledException) { }
                    throw;
                }
            }
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken); }
            catch (OperationCanceledException) { }
            throw scope.Fail(error);
        }).Timeout(TimeSpan.FromSeconds(1), static _ => new BrokenError());

        // When
        var running = work.RunExitAsync(new TimeEnvironment(clock), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (withRelease && independent)
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Equal("error getter", die.Exception.Message);
            Assert.Contains(die.Suppressed, item => item.Case.Value is Failed failed && ReferenceEquals(failed.Error, error));
        }
        else
        {
            Assert.True(exit.TryGetValue(out Err<BrokenError> _));
        }
        if (withRelease) Assert.IsType<Die>(released!.Value.Value);
        else Assert.Null(released);
    }

    [Fact]
    public async Task All_UnreadableErrorProjection_RecordsANewDefectOrder()
    {
        // Given
        var bodyFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new BrokenError();
        var other = new IOException("before getter");
        var first = Scope<BrokenError, EmptyEnvironment>.Gen<int>(async scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() => { projected.SetResult(); return Unit.Value; }));
            try { throw scope.Fail(error); }
            catch (ScopeFailure)
            {
                bodyFailed.SetResult();
                await otherFailed.Task;
                throw;
            }
        });
        Effect<Unit, BrokenError, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        var second = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Async(async _ =>
        {
            otherFailed.SetResult();
            await projected.Task;
            return Unit.Value;
        })).Use(_ => Effect<EmptyEnvironment>.Async<int>(async _ => { await bodyFailed.Task; throw other; }));

        // When
        var exit = await Effect.All([first, second], Concurrency.Parallel).RunExitAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(other, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Die { Exception.Message: "error getter" });
    }
}
