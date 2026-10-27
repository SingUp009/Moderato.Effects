using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class RecurrenceReuseTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RetryOrRepeat_OverlappingRunsAcrossBatches_PreserveResultsAndCancellation(bool retry, bool pendingPolicy, bool failure)
    {
        for (var batch = 0; batch < 3; batch++)
        {
            // Given
            const int COUNT = 32;
            var bodies = Enumerable.Range(0, COUNT).Select(_ => new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var policies = Enumerable.Range(0, COUNT).Select(_ => new TaskCompletionSource<Outcome<bool, Never>>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var callers = Enumerable.Range(0, COUNT).Select(_ => new CancellationTokenSource()).ToArray();
            var failures = Enumerable.Range(0, COUNT).Select(i => new OperationCanceledException($"failure {batch}:{i}")).ToArray();
            var ends = Enumerable.Range(0, COUNT).Select(i => retry
                ? Outcome<int, string>.Err($"error {batch}:{i}")
                : Outcome<int, string>.Ok(batch * COUNT + i)).ToArray();
            var source = new Effect<int, string, int>((env, _) => pendingPolicy ? new(ends[env]) : new(bodies[env].Task));
            var policy = new Effect<bool, Never, int>((env, _) => pendingPolicy ? new(policies[env].Task) : new(Outcome<bool, Never>.Ok(false)));
            var effect = retry ? source.Retry(_ => policy) : source.Repeat(_ => policy);
            var runs = new Task<Exit<int, string>>[COUNT];

            try
            {
                // When
                for (var i = 0; i < COUNT; i++)
                {
                    var running = effect.RunExitAsync(i, callers[i].Token);
                    Assert.False(running.IsCompleted);
                    runs[i] = running.AsTask();
                }
                for (var i = COUNT - 1; i >= 0; i--)
                {
                    if (failure)
                    {
                        if (i % 2 != 0) callers[i].Cancel();
                        if (pendingPolicy) policies[i].SetException(failures[i]);
                        else bodies[i].SetException(failures[i]);
                    }
                    else
                    {
                        if (pendingPolicy) policies[i].SetResult(Outcome<bool, Never>.Ok(false));
                        else bodies[i].SetResult(ends[i]);
                    }
                }
                var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                // Then
                for (var i = 0; i < COUNT; i++)
                {
                    if (failure)
                    {
                        if (i % 2 != 0)
                        {
                            Assert.True(results[i].TryGetValue(out Interrupted? _));
                        }
                        else
                        {
                            Assert.True(results[i].TryGetValue(out Die? die));
                            Assert.Same(failures[i], die.Exception);
                            Suppressed[] expected = retry && pendingPolicy
                                ? [new(new Failed($"error {batch}:{i}"), Relation.Sequential)]
                                : [];
                            Assert.Equal(expected, die.Suppressed);
                        }
                    }
                    else if (retry)
                    {
                        Assert.True(results[i].TryGetValue(out Err<string> err));
                        Assert.Equal($"error {batch}:{i}", err.Error);
                    }
                    else
                    {
                        Assert.True(results[i].TryGetValue(out Ok<int> ok));
                        Assert.Equal(batch * COUNT + i, ok.Value);
                    }
                }
            }
            finally
            {
                foreach (var caller in callers) caller.Dispose();
            }
        }
    }
}
