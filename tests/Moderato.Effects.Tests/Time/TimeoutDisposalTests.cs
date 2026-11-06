namespace Moderato.Effects.Tests;

public class TimeoutDisposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_TimerDisposalThrows_PreservesEarlierBodyDefect(bool asynchronous)
    {
        // Given
        var bodyFailure = new IOException("body defect");
        var timerFailure = new InvalidOperationException("timer disposal defect");
        var clock = new DisposalFailureClock(timerFailure, asynchronous);
        var effect = Effect<TimeEnvironment>.Sync<int>(() => throw bodyFailure)
            .Timeout(TimeSpan.FromSeconds(1));

        // When
        var running = effect.RunExitAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken).AsTask();
        clock.CompleteDisposal();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(bodyFailure, die.Exception);
        var suppressed = Assert.Single(die.Suppressed);
        Assert.Equal(Relation.Sequential, suppressed.Relation);
        Assert.Same(timerFailure, Assert.IsType<Die>(suppressed.Case.Value).Exception);
    }

    [Theory]
    [InlineData("Ok", false)]
    [InlineData("Ok", true)]
    [InlineData("Err", false)]
    [InlineData("Err", true)]
    [InlineData("Interrupted", false)]
    [InlineData("Interrupted", true)]
    public async Task Timeout_TimerDisposalThrows_PreservesBodyFailureAsContext(string outcome, bool asynchronous)
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var timerFailure = new InvalidOperationException("timer disposal defect");
        var clock = new DisposalFailureClock(timerFailure, asynchronous);
        Effect<int, string, TimeEnvironment> body = outcome switch
        {
            "Ok" => Effect<TimeEnvironment>.Succeed(42),
            "Err" => Effect<TimeEnvironment>.Fail<string>("body error"),
            _ => Effect<TimeEnvironment>.Async<int>(ct =>
            {
                caller.Cancel();
                return ValueTask.FromCanceled<int>(ct);
            }),
        };
        var effect = body.Timeout(TimeSpan.FromSeconds(1), _ => "timeout");

        // When
        var running = effect.RunExitAsync(new TimeEnvironment(clock), caller.Token).AsTask();
        clock.CompleteDisposal();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(timerFailure, die.Exception);
        if (outcome == "Ok")
        {
            Assert.Empty(die.Suppressed);
        }
        else
        {
            var suppressed = Assert.Single(die.Suppressed);
            Assert.Equal(Relation.Sequential, suppressed.Relation);
            if (outcome == "Err") Assert.Equal("body error", Assert.IsType<Failed>(suppressed.Case.Value).Error);
            else Assert.IsType<Interrupted>(suppressed.Case.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_TimerDisposalAndCancellationCallbackThrow_PreservesBothDefectsAndTimeout(bool asynchronous)
    {
        // Given
        var callbackFailure = new IOException("callback defect");
        var timerFailure = new InvalidOperationException("timer disposal defect");
        var clock = new DisposalFailureClock(timerFailure, asynchronous);
        var effect = Effect<TimeEnvironment>.Async<int>(ct =>
        {
            using var registration = ct.Register(() => throw callbackFailure);
            clock.Expire();
            return ValueTask.FromCanceled<int>(ct);
        }).Timeout(TimeSpan.FromSeconds(1));

        // When
        var running = effect.RunExitAsync(new TimeEnvironment(clock), TestContext.Current.CancellationToken).AsTask();
        clock.CompleteDisposal();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(timerFailure, die.Exception);
        Assert.Collection(die.Suppressed,
            item =>
            {
                Assert.Equal(Relation.Sequential, item.Relation);
                Assert.IsType<TimeoutError>(Assert.IsType<Failed>(item.Case.Value).Error);
            },
            item =>
            {
                Assert.Equal(Relation.Consequence, item.Relation);
                Assert.Same(callbackFailure, Assert.IsType<Die>(item.Case.Value).Exception);
            });
    }

    private sealed class DisposalFailureClock(Exception failure, bool asynchronous) : TimeProvider
    {
        private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TimerCallback? callback;
        private object? state;

        public void Expire() => callback!(state);

        public void CompleteDisposal()
        {
            if (asynchronous) disposal.TrySetException(failure);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            return new DisposalFailureTimer(failure, asynchronous ? disposal.Task : null);
        }
    }

    private sealed class DisposalFailureTimer(Exception failure, Task? disposal) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => throw failure;
        public ValueTask DisposeAsync() => disposal is null ? ValueTask.FromException(failure) : new(disposal);
    }
}
