namespace Moderato.Effects.Tests;

public class GroupReuseTests
{
    [Fact]
    public async Task AllSettled_OverlappingPendingGroups_KeepEachChildsValueAndExceptionAcrossBatches()
    {
        for (var batch = 0; batch < 3; batch++)
        {
            // Given
            var started = new TaskCompletionSource[8, 8];
            var released = new TaskCompletionSource[8, 8];
            var failures = new IOException[8, 8];
            for (var run = 0; run < 8; run++)
            {
                for (var child = 0; child < 8; child++)
                {
                    started[run, child] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    released[run, child] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    failures[run, child] = new($"batch {batch}, run {run}, child {child}");
                }
            }

            Effect<int, string, int>[] children = new Effect<int, string, int>[8];
            for (var child = 0; child < children.Length; child++)
            {
                var index = child;
                children[child] = Effect<int>.AccessAsync(async (environment, _) =>
                {
                    started[environment, index].SetResult();
                    await released[environment, index].Task;
                    if (index % 2 != 0) throw failures[environment, index];
                    return environment * 100 + index;
                });
            }

            // When
            var group = Effect.AllSettled(children, Concurrency.Parallel.WithMaxDegree(2));
            var running = Enumerable.Range(0, 8).Select(environment => group.RunAsync(environment, CancellationToken.None).AsTask()).ToArray();
            for (var pair = 0; pair < 4; pair++)
            {
                for (var run = 0; run < 8; run++)
                {
                    await started[run, pair * 2].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    await started[run, pair * 2 + 1].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                for (var run = 7; run >= 0; run--)
                {
                    released[run, pair * 2 + 1].SetResult();
                    released[run, pair * 2].SetResult();
                }
            }
            var results = await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            for (var run = 0; run < results.Length; run++)
            {
                Assert.True(results[run].TryGetOk(out var exits));
                Assert.Equal(8, exits.Length);
                for (var child = 0; child < exits.Length; child++)
                {
                    if (child % 2 == 0)
                    {
                        Assert.True(exits[child].TryGetValue(out Ok<int> ok));
                        Assert.Equal(run * 100 + child, ok.Value);
                    }
                    else
                    {
                        Assert.True(exits[child].TryGetValue(out Die? die));
                        Assert.Same(failures[run, child], die.Exception);
                        Assert.Empty(die.Suppressed);
                        Assert.Equal(0, die.OmittedSuppressed);
                    }
                }
            }
        }
    }
}
